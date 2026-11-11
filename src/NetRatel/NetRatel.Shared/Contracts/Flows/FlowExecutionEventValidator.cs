using System.Text.Json;

namespace NetRatel.Shared.Contracts.Flows;

public sealed record FlowExecutionEventValidationResultDto(bool Valid, IReadOnlyList<string> Issues);

/// <summary>Shape/budget validation only. Accepted source provenance and dispatch authority require server checks.</summary>
public static class FlowExecutionEventValidator
{
    public static FlowExecutionEventValidationResultDto Validate(FlowExecutionEventDto? value)
    {
        var issues = new List<string>();
        if (value is null) return new(false, ["event_required"]);
        if (value.SchemaVersion != FlowExecutionEventLimits.SchemaVersion) issues.Add("event_schema_unsupported");
        if (value.EventId == Guid.Empty || value.OccurrenceId == Guid.Empty) issues.Add("event_identity_required");
        if (value.TenantId <= 0) issues.Add("event_tenant_required");
        if (value.OccurredAtUtc == default || value.ObservedAtUtc == default) issues.Add("event_timestamp_required");
        if (!Enum.IsDefined(value.SourceKind)) issues.Add("event_source_unsupported");
        if (value.DefinitionSourceKind is { } definitionKind && (!Enum.IsDefined(definitionKind) ||
            value.SourceKind != FlowExecutionSourceKind.Request && definitionKind != value.SourceKind))
            issues.Add("event_definition_source_invalid");
        if (!Enum.IsDefined(value.Outcome)) issues.Add("event_outcome_unsupported");
        Required(value.ExecutionId, "execution_id");
        Required(value.AttemptId, "attempt_id");
        Required(value.RawStatus, "raw_status");
        var rawOutcome = FlowExecutionOutcomes.FromAuthoritativeStatus(value.RawStatus);
        if (rawOutcome is null) issues.Add("event_raw_status_not_terminal");
        else if (value.Outcome == FlowExecutionOutcome.Succeeded && rawOutcome != FlowExecutionOutcome.Succeeded)
            issues.Add("event_raw_success_mismatch");
        // Native task status can be Completed while authoritative exit/admission/cancellation
        // evidence supplies a non-success outcome. The immutable producer/ledger proves it.
        // Native status vocabularies differ. Only the trusted source writer/accepted ledger can
        // establish normalization (including a definitive admission denial recorded as Failed).
        Optional(value.CorrelationId, "correlation_id");
        Optional(value.RequestKind, "request_kind");
        Optional(value.ParentExecutionId, "parent_execution_id");
        Optional(value.RootExecutionId, "root_execution_id");
        Text(value.ReasonCode, 128, "reason_code");
        Text(value.Reason, FlowExecutionEventLimits.MaximumReasonLength, "reason");
        Text(value.Provenance, 128, "provenance");
        Definition(value.Definition, "definition");
        Target(value.Target, "target");
        if (value.Aggregate is { } aggregate)
        {
            Hash(aggregate.TargetPlanDigest, "target_plan_hash");
            var counts = aggregate.Counts;
            if (value.SourceKind != FlowExecutionSourceKind.Job &&
                !(value.SourceKind == FlowExecutionSourceKind.Request && value.DefinitionSourceKind == FlowExecutionSourceKind.Job) ||
                value.Target?.AgentId != aggregate.LegacyTargetAgentId ||
                aggregate.LegacyTargetAgentId is { } legacy && (legacy == Guid.Empty || aggregate.TargetCount != 1) ||
                aggregate.TargetCount is < 1 or > 10000 || aggregate.TargetPlanDigest is not { Length: 64 } || counts is null ||
                counts.Queued != 0 || counts.Running != 0 || counts.Succeeded < 0 || counts.Failed < 0 || counts.Skipped < 0 ||
                counts.Cancelled < 0 || counts.TimedOut < 0 || counts.Unknown < 0 ||
                (long)counts.Succeeded + counts.Failed + counts.Skipped + counts.Cancelled + counts.TimedOut + counts.Unknown != aggregate.TargetCount)
                issues.Add("event_aggregate_invalid");
            if (value.Outcome == FlowExecutionOutcome.Succeeded && counts?.Succeeded != aggregate.TargetCount)
                issues.Add("event_aggregate_success_mismatch");
        }
        if (value.CompletedAtUtc < value.StartedAtUtc) issues.Add("event_timing_invalid");
        if (value.Causation is { } cause)
        {
            Required(cause.RootOccurrenceId, "causation_root");
            if (cause.HopCount is < 0 or > FlowExecutionEventLimits.MaximumHopCount ||
                cause.RecoveryAttempt is < 0 or > FlowExecutionEventLimits.MaximumRecoveryAttempts)
                issues.Add("event_causation_budget_exceeded");
            if (cause.FlowRunId == Guid.Empty || cause.NodeId == Guid.Empty || cause.ParentEventId == Guid.Empty)
                issues.Add("event_causation_identity_invalid");
        }
        if (value.Replay is { } replay)
        {
            if (!Enum.IsDefined(replay.SourceKind)) issues.Add("event_replay_source_unsupported");
            Definition(replay.Definition, "replay_definition");
            Target(replay.Target, "replay_target");
            Required(replay.SourceExecutionId, "replay_execution_id");
            Required(replay.SourceAttemptId, "replay_attempt_id");
            Text(replay.InputsReference, 512, "replay_inputs_reference");
            Text(replay.UnsupportedReason, 128, "replay_unsupported_reason");
            Hash(replay.TargetPlanDigest, "replay_target_plan_hash");
            if (replay.TargetPlanDigest is not null && (replay.SourceKind != FlowExecutionSourceKind.Job ||
                value.Aggregate?.TargetPlanDigest != replay.TargetPlanDigest && !(value.SourceKind == FlowExecutionSourceKind.Request &&
                    replay.Target?.AgentId is not null && replay.Target.AgentId == value.Target?.AgentId)))
                issues.Add("event_replay_target_plan_mismatch");
            if (replay.Supported && (string.IsNullOrWhiteSpace(replay.Definition?.Id) ||
                string.IsNullOrWhiteSpace(replay.Definition?.Revision) || replay.Definition?.ContentHash is not { Length: 64 } ||
                replay.SourceKind is not (FlowExecutionSourceKind.Job or FlowExecutionSourceKind.Script) ||
                replay.Target?.AgentId is null && !(replay.SourceKind == FlowExecutionSourceKind.Job &&
                    value.Aggregate is not null && replay.TargetPlanDigest == value.Aggregate.TargetPlanDigest) ||
                string.IsNullOrWhiteSpace(replay.InputsReference) || replay.Target?.AgentId != value.Target?.AgentId ||
                !string.Equals(replay.InputsReference, replay.SourceKind switch
                {
                    FlowExecutionSourceKind.Job => "job-run:" + replay.SourceExecutionId,
                    FlowExecutionSourceKind.Script => "script-execution:" + replay.SourceExecutionId,
                    FlowExecutionSourceKind.Request => "request:" + replay.SourceExecutionId,
                    _ => null
                }, StringComparison.Ordinal)))
                issues.Add("event_replay_material_unpinned");
        }
        if (value.Results is { } results)
        {
            if (results.Count > FlowExecutionEventLimits.MaximumReferences) issues.Add("event_reference_limit");
            else foreach (var reference in results)
            {
                if (reference is null) { issues.Add("event_reference_required"); continue; }
                Required(reference.Kind, "reference_kind");
                Required(reference.Id, "reference_id");
                Hash(reference.ContentHash, "reference_hash");
            }
        }
        if (issues.Count == 0 &&
            JsonSerializer.SerializeToUtf8Bytes(value, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto).Length >
                FlowExecutionEventLimits.MaximumEnvelopeBytes)
            issues.Add("event_byte_limit");
        return new(issues.Count == 0, issues);

        void Required(string? text, string field)
        {
            if (string.IsNullOrWhiteSpace(text)) issues.Add("event_" + field + "_required");
            else Optional(text, field);
        }
        void Optional(string? text, string field)
        {
            Text(text, FlowExecutionEventLimits.MaximumIdentifierLength, field);
            if (text is not null && text.Any(char.IsControl)) issues.Add("event_" + field + "_invalid");
        }
        void Text(string? text, int maximum, string field)
        {
            if (text is not null && !FlowGraphValidator.IsBoundedText(text, maximum, allowEmpty: true, multiline: true))
                issues.Add("event_" + field + "_invalid");
        }
        void Hash(string? hash, string field)
        {
            if (hash is not null && (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)))
                issues.Add("event_" + field + "_invalid");
        }
        void Definition(FlowExecutionDefinitionDto? definition, string field)
        {
            if (definition is null) { issues.Add("event_" + field + "_required"); return; }
            Optional(definition.Id, field + "_id");
            Text(definition.Name, 512, field + "_name");
            Optional(definition.Revision, field + "_revision");
            Hash(definition.ContentHash, field + "_hash");
        }
        void Target(FlowExecutionTargetDto? target, string field)
        {
            if (target is null) { issues.Add("event_" + field + "_required"); return; }
            if (target.AgentId == Guid.Empty) issues.Add("event_" + field + "_agent_invalid");
            Text(target.DisplayName, 512, field + "_name");
            Text(target.HostName, 512, field + "_hostname");
        }
    }
}

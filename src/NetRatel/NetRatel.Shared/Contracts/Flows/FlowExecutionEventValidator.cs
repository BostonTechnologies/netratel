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
        if (!Enum.IsDefined(value.SourceKind)) issues.Add("event_source_unsupported");
        if (!Enum.IsDefined(value.Outcome)) issues.Add("event_outcome_unsupported");
        Required(value.ExecutionId, "execution_id");
        Required(value.AttemptId, "attempt_id");
        Required(value.RawStatus, "raw_status");
        if (FlowExecutionOutcomes.FromAuthoritativeStatus(value.RawStatus) != value.Outcome)
            issues.Add("event_raw_outcome_mismatch");
        Optional(value.CorrelationId, "correlation_id");
        Optional(value.RequestKind, "request_kind");
        Optional(value.ParentExecutionId, "parent_execution_id");
        Optional(value.RootExecutionId, "root_execution_id");
        Text(value.ReasonCode, 128, "reason_code");
        Text(value.Reason, FlowExecutionEventLimits.MaximumReasonLength, "reason");
        Text(value.Provenance, 128, "provenance");
        Definition(value.Definition, "definition");
        Target(value.Target, "target");
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
            Text(replay.InputsReference, 256, "replay_inputs_reference");
            Text(replay.UnsupportedReason, 128, "replay_unsupported_reason");
            if (replay.Supported && (string.IsNullOrWhiteSpace(replay.Definition?.Id) ||
                string.IsNullOrWhiteSpace(replay.Definition?.Revision) || replay.Target?.AgentId is null ||
                string.IsNullOrWhiteSpace(replay.InputsReference) || replay.Target != value.Target ||
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
            if (text is not null && (text.Length > maximum || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))))
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

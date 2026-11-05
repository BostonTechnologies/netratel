using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowIncidentEnrichmentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Mapping_before_AI_receives_the_exact_terminal_findings_once_before_incident_preparation()
    {
        var fixture = Create();
        var original = JsonSerializer.Serialize(fixture.Incident.Event, Json);
        var final = FlowIncidentEnrichment.Prepare(fixture.Incident, fixture.AiDraft, fixture.AiResult);
        Assert.Equal("Synthetic incident", final.Fields.Title);
        Assert.Contains("Mapped before investigation.", final.Fields.Description);
        Assert.Contains("Synthetic bounded diagnosis.", final.Fields.Description);
        Assert.Contains("AI investigation (conclusions, separate from original facts)", final.Fields.Description);
        Assert.Equal(1, final.Fields.Description.Split("Original execution facts", StringSplitOptions.None).Length - 1);
        Assert.Equal("Mapped before investigation.", fixture.Incident.Fields.Description);
        Assert.Equal(original, JsonSerializer.Serialize(fixture.Incident.Event, Json));
        Assert.Throws<ArgumentException>(() => FlowIncidentEnrichment.Prepare(fixture.Incident, fixture.AiDraft,
            fixture.AiResult with { MessageId = Guid.NewGuid(), Summary = "A later manual reply is not this turn." }));
        Assert.Throws<ArgumentException>(() => FlowIncidentEnrichment.Prepare(fixture.Incident,
            fixture.AiDraft with { Event = fixture.AiDraft.Event with { Authority = new("connection-owner") } }, fixture.AiResult));
        Assert.Throws<ArgumentException>(() => FlowIncidentEnrichment.Prepare(fixture.Incident,
            fixture.AiDraft, fixture.AiResult with { ActionNodeId = fixture.Incident.ActionNodeId }));
    }

    [Fact]
    public void Source_presentation_values_remain_usable_in_single_line_typed_titles()
    {
        var fixture = Create();
        var source = fixture.Original with
        {
            Definition = fixture.Original.Definition with { Name = new string('n', 200) },
            Target = fixture.Original.Target with { DisplayName = "Synthetic target\nsecond\tline" }
        };
        var values = FlowExecutionEvidenceFormatter.Values(source);
        Assert.Contains("[Truncated for display", values["definitionName"]);
        Assert.Equal("Synthetic target second line", values["targetName"]);
        Assert.True(FlowGraphValidator.IsBoundedText(values["definitionName"] + ": " + values["outcome"],
            FlowLimits.MaximumTitleLength));
        Assert.Equal(new string('n', 200), source.Definition.Name);
        Assert.Equal("Synthetic target\nsecond\tline", source.Target.DisplayName);
    }

    [Theory]
    [InlineData(FlowAiResultStatus.Queued)]
    [InlineData(FlowAiResultStatus.Processing)]
    [InlineData(FlowAiResultStatus.AwaitingApproval)]
    public void Pending_AI_evidence_cannot_be_frozen_as_final_findings(FlowAiResultStatus status)
    {
        var fixture = Create();
        Assert.Throws<InvalidOperationException>(() => FlowIncidentEnrichment.Prepare(fixture.Incident, fixture.AiDraft,
            fixture.AiResult with { Status = status, CompletedAtUtc = null }));
        Assert.Throws<ArgumentException>(() => FlowIncidentEnrichment.Prepare(fixture.Incident, investigation: fixture.AiResult));
    }

    [Fact]
    public void Unavailable_investigation_keeps_original_facts_without_findings_or_recovery_claims()
    {
        var fixture = Create();
        var unsupported = fixture.AiResult with { Status = FlowAiResultStatus.Unsupported, InvestigationId = Guid.Empty,
            Summary = "This unconfirmed text must not become findings." };
        var final = FlowIncidentEnrichment.Prepare(fixture.Incident, fixture.AiDraft, unsupported);
        Assert.Contains("Outcome: Unsupported", final.Fields.Description);
        Assert.Contains("Investigation: Not started", final.Fields.Description);
        Assert.Contains("Original execution facts", final.Fields.Description);
        Assert.DoesNotContain("This unconfirmed text", final.Fields.Description);
        Assert.DoesNotContain("Recovered (", final.Fields.Description);
        var legacy = fixture.Incident with { Event = fixture.Incident.Event with
        {
            ExecutionEvent = null, Data = new(Guid.NewGuid(), Guid.NewGuid(), "Existing alert", "Existing target",
                "CPU", "usage", "Error", 95, null, fixture.Original.OccurredAtUtc)
        } };
        var bytes = JsonSerializer.Serialize(legacy, Json);
        Assert.Same(legacy, FlowIncidentEnrichment.Prepare(legacy));
        Assert.Equal(bytes, JsonSerializer.Serialize(legacy, Json));
    }

    [Fact]
    public async Task Reader_distinguishes_pending_from_missing_and_uses_the_saved_automated_result_not_a_later_turn()
    {
        var fixture = Create();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new OrchestratorDbContext(options);
        var unsupported = fixture.AiResult with { Status = FlowAiResultStatus.Unsupported, Summary = null,
            Evidence = null, Recommendations = null };
        var action = new FlowActionRecord
        {
            TenantId = fixture.Incident.TenantId, RunId = fixture.Incident.RunId, NodeId = fixture.AiDraft.ActionNodeId,
            IdempotencyKey = fixture.AiDraft.IdempotencyKey, Status = FlowActionStatus.WaitingForAi,
            DraftJson = JsonSerializer.Serialize(fixture.AiDraft, Json), AiResultJson = JsonSerializer.Serialize(unsupported, Json)
        };
        db.FlowActions.Add(action);
        var manualMessage = Guid.NewGuid();
        db.AiInvestigations.Add(new() { Id = unsupported.InvestigationId, TenantId = 17, RequestId = Guid.NewGuid(),
            Title = "Synthetic saved investigation", CreatedBy = "source-publisher", FlowRunId = fixture.Incident.RunId,
            SourceActionKey = fixture.AiDraft.IdempotencyKey, ActiveMessageId = manualMessage, Automated = false });
        db.AiInvestigationEvents.Add(new() { InvestigationId = unsupported.InvestigationId, Sequence = 1, Type = "assistant",
            MessageId = manualMessage, Text = "Later manual success must not be selected.", CreatedAtUtc = fixture.AiResult.ObservedAtUtc.AddHours(1) });
        await db.SaveChangesAsync();
        var reader = new FlowIncidentEvidenceReader(db);
        var pending = await reader.ReadAsync(fixture.Incident, fixture.AiDraft.ActionNodeId);
        Assert.Equal(FlowIncidentEvidenceDisposition.Waiting, pending.Disposition);
        Assert.Null(pending.Draft);
        var missing = await reader.ReadAsync(fixture.Incident, Guid.NewGuid());
        Assert.Equal(FlowIncidentEvidenceDisposition.Unavailable, missing.Disposition);
        action.Status = FlowActionStatus.Failed;
        await db.SaveChangesAsync();
        var ready = await reader.ReadAsync(fixture.Incident, fixture.AiDraft.ActionNodeId);
        Assert.Equal(FlowIncidentEvidenceDisposition.Ready, ready.Disposition);
        Assert.NotNull(ready.Draft);
        Assert.Contains("Outcome: Unsupported", ready.Draft.Fields.Description);
        Assert.DoesNotContain("Later manual success", ready.Draft.Fields.Description);
        action.AiResultJson = JsonSerializer.Serialize(unsupported with { TenantId = 18 }, Json);
        await db.SaveChangesAsync();
        var invalid = await reader.ReadAsync(fixture.Incident, fixture.AiDraft.ActionNodeId);
        Assert.Equal(FlowIncidentEvidenceDisposition.Unavailable, invalid.Disposition);
        Assert.Null(invalid.Draft);
        Assert.Single(await db.FlowActions.ToListAsync());
    }

    [Fact]
    public void Canonical_receipt_and_native_proof_are_required_and_actual_execution_order_controls_latest_rerun()
    {
        var fixture = Create();
        var first = Result(fixture, fixture.FirstRerunId, "9007199254740994", FlowExecutionOutcome.Succeeded, 1);
        var second = Result(fixture, fixture.SecondRerunId, "9007199254740995", FlowExecutionOutcome.Failed, 2);
        var outputs = new Dictionary<Guid, JsonElement>
        {
            [fixture.SecondRerunId] = JsonSerializer.SerializeToElement(second, Json),
            [fixture.FirstRerunId] = JsonSerializer.SerializeToElement(first, Json)
        };
        var recorded = FlowRecoveryEvidence.FromOutputs(fixture.Lease, outputs, [fixture.FirstRerunId, fixture.SecondRerunId]);
        Assert.Equal(fixture.SecondRerunId, recorded[^1].NodeId);
        Assert.Equal("Unresolved", FlowExecutionEvidenceFormatter.RecoveryDisposition(fixture.Original, fixture.Incident.RunId, recorded));
        outputs.Remove(fixture.SecondRerunId);
        var success = Assert.Single(FlowRecoveryEvidence.FromOutputs(fixture.Lease, outputs, [fixture.FirstRerunId]));
        Assert.StartsWith("Recovered", FlowExecutionEvidenceFormatter.RecoveryDisposition(fixture.Original, fixture.Incident.RunId, [success]));
        outputs[fixture.FirstRerunId] = JsonSerializer.SerializeToElement(first with { TerminalEvent = null }, Json);
        var unproven = Assert.Single(FlowRecoveryEvidence.FromOutputs(fixture.Lease, outputs, [fixture.FirstRerunId]));
        Assert.Equal(FlowRecoveryActionState.Unknown, unproven.State);
        var terminal = Assert.IsType<FlowExecutionEventDto>(first.TerminalEvent);
        var cause = Assert.IsType<FlowExecutionCausationDto>(terminal.Causation);
        outputs[fixture.FirstRerunId] = JsonSerializer.SerializeToElement(first with
            { TerminalEvent = terminal with { Causation = cause with { FlowRunId = Guid.NewGuid() } } }, Json);
        var foreign = Assert.Single(FlowRecoveryEvidence.FromOutputs(fixture.Lease, outputs, [fixture.FirstRerunId]));
        Assert.Equal(FlowRecoveryActionState.Unknown, foreign.State);
        Assert.Null(foreign.TerminalEvent);
        Assert.Throws<ArgumentException>(() => FlowRecoveryEvidence.FromOutputs(fixture.Lease, outputs, []));
        outputs[fixture.FirstRerunId] = JsonSerializer.SerializeToElement(first with { Disposition = FlowExecutionActionDisposition.Pending,
            TerminalEvent = null }, Json);
        Assert.Equal(FlowRecoveryActionState.Pending,
            Assert.Single(FlowRecoveryEvidence.FromOutputs(fixture.Lease, outputs, [fixture.FirstRerunId])).State);
    }

    [Theory]
    [InlineData(FlowExecutionActionDisposition.Denied, "rerun-attempts-exhausted", FlowRecoveryActionState.Exhausted)]
    [InlineData(FlowExecutionActionDisposition.Denied, "execution-causation-exhausted", FlowRecoveryActionState.Exhausted)]
    [InlineData(FlowExecutionActionDisposition.Denied, "execution-authority-denied", FlowRecoveryActionState.Denied)]
    [InlineData(FlowExecutionActionDisposition.Unknown, "original-outcome-unknown", FlowRecoveryActionState.Unknown)]
    [InlineData(FlowExecutionActionDisposition.Unsupported, "original-execution-not-replayable", FlowRecoveryActionState.Unsupported)]
    public void Canonical_denied_exhausted_unknown_and_unsupported_codes_remain_explicit(
        FlowExecutionActionDisposition disposition, string code, FlowRecoveryActionState expected)
    {
        var fixture = Create();
        var outputs = new Dictionary<Guid, JsonElement>
        {
            [fixture.FirstRerunId] = JsonSerializer.SerializeToElement(new FlowExecutionActionResult(disposition, code), Json)
        };
        var attempt = Assert.Single(FlowRecoveryEvidence.FromOutputs(fixture.Lease, outputs, [fixture.FirstRerunId]));
        Assert.Equal(expected, attempt.State);
        Assert.Equal(code, attempt.Code);
        var final = FlowIncidentEnrichment.Prepare(fixture.Incident, fixture.AiDraft, fixture.AiResult, [attempt]);
        Assert.Contains("Code: " + code, final.Fields.Description);
        Assert.DoesNotContain("Recovered (", final.Fields.Description);
    }

    [Fact]
    public void Long_bounded_repair_history_retains_the_final_rerun_proof_before_truncating_display()
    {
        var fixture = Create();
        var result = Result(fixture, fixture.FirstRerunId, "9007199254740994", FlowExecutionOutcome.Succeeded, 1);
        var attempts = Enumerable.Range(0, 24).Select(index => new FlowRecoveryAttemptEvidence(Guid.NewGuid(), 1,
            "Synthetic permitted repair " + index, FlowRecoveryActionState.Denied, false)).ToList();
        attempts.Add(new(fixture.FirstRerunId, 1, "Linked original rerun", FlowRecoveryActionState.Succeeded, true,
            result.Receipt?.ExecutionId, result.TerminalEvent));
        var final = FlowIncidentEnrichment.Prepare(fixture.Incident, fixture.AiDraft, fixture.AiResult, attempts);
        Assert.Contains("Recovered (linked rerun terminal success verified)", final.Fields.Description);
        Assert.Contains("Accepted execution: 9007199254740994", final.Fields.Description);
        Assert.Contains("/jobs/42/runs/9007199254740994", final.Fields.Description);
        Assert.Contains("[Truncated for display", final.Fields.Description);
        Assert.True(FlowGraphValidator.IsBoundedText(final.Fields.Description, FlowLimits.MaximumDescriptionLength, multiline: true));
    }

    private sealed record Fixture(FlowIncidentActionDraft Incident, FlowIncidentActionDraft AiDraft, FlowAiResultDto AiResult,
        FlowExecutionEventDto Original, FlowRunLease Lease, Guid FirstRerunId, Guid SecondRerunId);

    private static Fixture Create()
    {
        var at = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
        var occurrence = Guid.NewGuid();
        var definition = new FlowExecutionDefinitionDto("42", "Synthetic original job", "7", new string('a', 64));
        var target = new FlowExecutionTargetDto(Guid.NewGuid(), "Synthetic bound target", "worker.example.test");
        var source = new FlowExecutionEventDto(Guid.NewGuid(), occurrence, 17, FlowExecutionSourceKind.Job, definition,
            "9007199254740993", "9007199254740993", target, FlowExecutionOutcome.Failed, "Failed", at, at)
        {
            StartedAtUtc = at.AddSeconds(-10), CompletedAtUtc = at, Reason = "Original synthetic failure.", Provenance = "job-terminal-ready",
            Causation = new(occurrence.ToString("N")),
            Replay = new(FlowExecutionSourceKind.Job, definition, target, "job-run:9007199254740993",
                "9007199254740993", "9007199254740993", true)
        };
        var runId = Guid.NewGuid(); var versionId = Guid.NewGuid(); var sourceInstance = Guid.NewGuid();
        var envelope = JsonSerializer.Deserialize<FlowEventEnvelope>(JsonSerializer.Serialize(new
        {
            tenantId = 17, eventId = source.EventId, occurrenceId = source.OccurrenceId, flowVersionId = versionId,
            occurredAtUtc = at, authority = new FlowExecutionAuthorityDto("source-publisher", "source-credential"),
            executionEvent = source
        }, Json), Json) ?? throw new InvalidOperationException("Missing source envelope.");
        var incident = new FlowIncidentActionDraft(17, runId, Guid.NewGuid(), Guid.NewGuid(), 7, sourceInstance,
            "synthetic:incident:1", envelope, new("Synthetic incident", "Mapped before investigation."));
        var ai = incident with { ActionNodeId = Guid.NewGuid(), ConnectorId = Guid.NewGuid(), IdempotencyKey = "synthetic:ai:1",
            Kind = FlowActionKind.NetclawInvestigation };
        var result = new FlowAiResultDto(17, runId, versionId, ai.ActionNodeId, Guid.NewGuid(), FlowAiCorrelation.MessageId(ai),
            FlowAiResultStatus.Completed, at.AddSeconds(10), at.AddSeconds(11), "Synthetic bounded diagnosis.",
            "Protected synthetic evidence reference.", "Use the configured pinned repair.");
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        // Node creation order differs from the recorded execution order.
        var graph = new FlowGraphDto(2,
        [
            new(second, FlowNodeKind.RerunOriginalExecution, "Second deliberate rerun", new(400, 0)) { Rerun = new(2) },
            new(first, FlowNodeKind.RerunOriginalExecution, "First deliberate rerun", new(200, 0)) { Rerun = new() }
        ], [], new(0, 0, 1));
        var version = new FlowVersionDto(versionId, Guid.NewGuid(), 17, 1, graph, new string('b', 64), "source-publisher", at);
        var lease = new FlowRunLease(runId, Guid.NewGuid(), Guid.NewGuid(), 1, at.AddMinutes(5), sourceInstance, version, envelope, 1)
            { CreatedAtUtc = at };
        return new(incident, ai, result, source, lease, first, second);
    }

    private static FlowExecutionActionResult Result(Fixture fixture, Guid nodeId, string executionId,
        FlowExecutionOutcome outcome, int attempt)
    {
        var source = fixture.Original;
        var terminal = source with
        {
            EventId = Guid.NewGuid(), OccurrenceId = Guid.NewGuid(), ExecutionId = executionId, AttemptId = executionId,
            Outcome = outcome, RawStatus = outcome == FlowExecutionOutcome.Succeeded ? "Completed" : "Failed", Replay = null,
            ParentExecutionId = source.ExecutionId, RootExecutionId = source.ExecutionId,
            StartedAtUtc = source.OccurredAtUtc.AddSeconds(attempt * 20),
            CompletedAtUtc = source.OccurredAtUtc.AddSeconds(attempt * 20 + 10),
            OccurredAtUtc = source.OccurredAtUtc.AddSeconds(attempt * 20 + 10),
            ObservedAtUtc = source.OccurredAtUtc.AddSeconds(attempt * 20 + 11),
            Reason = outcome == FlowExecutionOutcome.Succeeded ? null : "Synthetic rerun failed.",
            Causation = new(source.Causation?.RootOccurrenceId ?? source.OccurrenceId.ToString("D"),
                source.EventId, fixture.Incident.RunId, nodeId, 1, attempt)
        };
        return new(outcome == FlowExecutionOutcome.Succeeded ? FlowExecutionActionDisposition.Succeeded : FlowExecutionActionDisposition.Failed,
            "synthetic-native-result", new(Guid.NewGuid(), ExecutionAdmissionKind.Job, executionId, source.ExecutionId, attempt),
            TerminalEvent: terminal);
    }
}

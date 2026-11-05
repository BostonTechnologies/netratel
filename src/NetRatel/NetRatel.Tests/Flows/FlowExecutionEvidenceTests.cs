using System.Text.Json;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowExecutionEvidenceTests
{
    private static readonly Guid RunId = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid VersionId = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid NodeId = Guid.Parse("c0000000-0000-0000-0000-000000000001");

    [Fact]
    public void Execution_mapping_keeps_native_facts_names_links_and_AI_conclusions_separate()
    {
        var original = Source();
        var serialized = JsonSerializer.Serialize(original);
        var ai = Ai(original) with { Summary = "Likely exhausted storage.", Evidence = "Volume report.", Recommendations = "Use the configured repair." };
        Assert.NotEqual(original.EventId, ai.MessageId);
        var result = FlowExecutionEvidenceFormatter.Map(new("{definitionName}: {outcome}",
            "Target {targetName}; native state {rawStatus}; {executionLink}"), original, 17, RunId, VersionId, ai);
        Assert.Equal("Synthetic nightly job: Failed", result.Title);
        Assert.Contains("Original execution facts", result.Description);
        Assert.Contains("Raw status: Failed", result.Description);
        Assert.Contains("Target: Synthetic worker", result.Description);
        Assert.Contains("Revision: 7", result.Description);
        Assert.Contains("Exit code: 12", result.Description);
        Assert.Contains("/jobs/42/runs/9007199254740993", result.Description);
        Assert.Contains("AI investigation (conclusions, separate from original facts)", result.Description);
        Assert.Contains("Likely exhausted storage.", result.Description);
        Assert.DoesNotContain("monitoring alert", result.Description);
        Assert.Equal(serialized, JsonSerializer.Serialize(original));
        var mappedBeforeAi = new FlowIncidentFieldsDto("Synthetic incident", "Mapped before investigation.");
        var final = FlowExecutionEvidenceFormatter.Enrich(mappedBeforeAi, original, 17, RunId, VersionId, ai);
        Assert.Contains("Mapped before investigation.", final.Description);
        Assert.Contains("Likely exhausted storage.", final.Description);
        Assert.Equal("Mapped before investigation.", mappedBeforeAi.Description);
    }

    [Fact]
    public void Accepted_execution_or_successful_repair_does_not_prove_original_recovery()
    {
        var original = Source();
        var pending = new FlowRecoveryAttemptEvidence(NodeId, 1, "Rerun original", FlowRecoveryActionState.Pending, true, "9007199254740994");
        Assert.Equal("Pending (not verified)", FlowExecutionEvidenceFormatter.RecoveryDisposition(original, RunId, [pending]));
        var repair = pending with { IsOriginalRerun = false, State = FlowRecoveryActionState.Succeeded, ActionName = "Approved cleanup" };
        Assert.Equal("Original execution recovery not verified", FlowExecutionEvidenceFormatter.RecoveryDisposition(original, RunId, [repair]));
        Assert.StartsWith("Unknown", FlowExecutionEvidenceFormatter.RecoveryDisposition(original, RunId,
            [pending with { State = FlowRecoveryActionState.Succeeded }]));
    }

    [Fact]
    public void Only_the_exact_authoritative_linked_rerun_can_be_reported_recovered()
    {
        var original = Source();
        var terminal = original with
        {
            EventId = Guid.NewGuid(), ExecutionId = "9007199254740994", AttemptId = "2",
            Outcome = FlowExecutionOutcome.Succeeded, RawStatus = "Completed", Replay = null,
            ParentExecutionId = original.ExecutionId, RootExecutionId = original.ExecutionId,
            Causation = new(original.OccurrenceId.ToString("D"), original.EventId, RunId, NodeId, 1, 1)
        };
        var rerun = new FlowRecoveryAttemptEvidence(NodeId, 1, "Rerun original", FlowRecoveryActionState.Succeeded,
            true, terminal.ExecutionId, terminal);
        Assert.StartsWith("Recovered", FlowExecutionEvidenceFormatter.RecoveryDisposition(original, RunId, [rerun]));
        Assert.StartsWith("Unknown", FlowExecutionEvidenceFormatter.RecoveryDisposition(original, RunId,
            [rerun with { TerminalEvent = terminal with { TenantId = 18 } }]));
        Assert.StartsWith("Unknown", FlowExecutionEvidenceFormatter.RecoveryDisposition(original, RunId,
            [rerun with { TerminalEvent = terminal with { Causation = terminal.Causation! with { RecoveryAttempt = 2 } } }]));
        Assert.StartsWith("Unknown", FlowExecutionEvidenceFormatter.RecoveryDisposition(original, RunId,
            [rerun with { TerminalEvent = terminal with { Outcome = FlowExecutionOutcome.Unknown } }]));
        Assert.StartsWith("Unknown", FlowExecutionEvidenceFormatter.RecoveryDisposition(original, RunId,
            [rerun with { TerminalEvent = terminal with { Provenance = "sample" } }]));
        Assert.StartsWith("Unknown", FlowExecutionEvidenceFormatter.RecoveryDisposition(original, RunId,
            [rerun with { TerminalEvent = terminal with { Provenance = "request-state-commit" } }]));
        Assert.StartsWith("Unknown", FlowExecutionEvidenceFormatter.RecoveryDisposition(original with
            { Provenance = "sample" }, RunId, [rerun]));
        Assert.StartsWith("Unknown", FlowExecutionEvidenceFormatter.RecoveryDisposition(original, RunId,
            [rerun with { TerminalEvent = terminal with { Causation = terminal.Causation! with { RootOccurrenceId = "different-root" } } }]));
        Assert.StartsWith("Unknown", FlowExecutionEvidenceFormatter.RecoveryDisposition(original, RunId,
            [rerun with { TerminalEvent = terminal with { RawStatus = "Running" } }]));
        Assert.StartsWith("Unknown", FlowExecutionEvidenceFormatter.RecoveryDisposition(original with
            { Replay = original.Replay! with { Supported = false } }, RunId, [rerun]));
        Assert.StartsWith("Unknown", FlowExecutionEvidenceFormatter.RecoveryDisposition(original with
            { Outcome = FlowExecutionOutcome.Unknown, RawStatus = "DeliveryUnknown" }, RunId, [rerun]));
    }

    [Fact]
    public void Missing_and_empty_results_and_unsuccessful_AI_findings_are_labelled_without_invented_data()
    {
        var original = Source();
        Assert.Contains("Unavailable (not recorded)", FlowExecutionEvidenceFormatter.DescribeOriginal(original));
        Assert.Contains("Empty (recorded)", FlowExecutionEvidenceFormatter.DescribeOriginal(original with { Results = [] }));
        var references = original with { Results = Enumerable.Range(1, 5).Select(x =>
            new FlowExecutionDataReferenceDto("diagnostic", "synthetic-" + x, Available: true)).ToArray() };
        var referenceText = FlowExecutionEvidenceFormatter.DescribeOriginal(references);
        Assert.Contains("source reports available; access checked separately", referenceText);
        Assert.Contains("Additional result references omitted from this description", referenceText);
        Assert.DoesNotContain("retained in flow history", referenceText);
        var ai = Ai(original) with { Status = FlowAiResultStatus.Timeout, Summary = "Unconfirmed successful repair" };
        var result = FlowExecutionEvidenceFormatter.Map(new("{definitionName}", "Original outcome {outcome}"), original, 17, RunId, VersionId, ai);
        Assert.Contains("Outcome: Timeout", result.Description);
        Assert.Contains("No successful AI findings were confirmed", result.Description);
        Assert.DoesNotContain("Unconfirmed successful repair", result.Description);
        Assert.Throws<ArgumentException>(() => FlowExecutionEvidenceFormatter.Map(new("Failure", "Evidence"), original,
            18, RunId, VersionId, ai));
        Assert.Throws<ArgumentException>(() => FlowExecutionEvidenceFormatter.Map(new("Failure", "Evidence"), original,
            17, Guid.NewGuid(), VersionId, ai));
    }

    [Fact]
    public void Bounded_incident_and_AI_terminal_evidence_redact_before_truncation()
    {
        var original = Source() with { Reason = "api_key=synthetic-private-value " + new string('界', 1800) };
        var ai = Ai(original) with { Summary = "Bearer synthetic-token " + string.Concat(Enumerable.Repeat("🦦", 2500)) };
        var result = FlowExecutionEvidenceFormatter.Map(new("{definitionName}", new string('m', 4000)), original, 17, RunId, VersionId, ai);
        Assert.True(result.Description.Length <= FlowLimits.MaximumDescriptionLength);
        Assert.Contains("[Truncated", result.Description);
        Assert.Contains("Execution: 9007199254740993", result.Description);
        Assert.DoesNotContain("synthetic-private-value", result.Description);
        Assert.DoesNotContain("synthetic-token", result.Description);
        Assert.True(FlowGraphValidator.IsBoundedText(result.Description, FlowLimits.MaximumDescriptionLength, multiline: true));
        var safe = InvestigationStore.SafeResultText("password=synthetic-private-value\0\nBearer synthetic-token", 120);
        Assert.DoesNotContain("synthetic-private-value", safe);
        Assert.DoesNotContain("synthetic-token", safe);
        Assert.DoesNotContain("\0", safe);
        Assert.Contains("[REDACTED]", safe);
    }

    [Fact]
    public void Script_links_use_the_canonical_request_identity_and_preserve_large_identifiers()
    {
        var original = Source() with { SourceKind = FlowExecutionSourceKind.Script, Replay = null };
        var description = FlowExecutionEvidenceFormatter.DescribeOriginal(original);
        Assert.Contains("Execution: 9007199254740993", description);
        Assert.Contains("Execution view: /tasks/request/9007199254740993", description);
        var supported = original with { ExecutionId = "42" };
        Assert.Contains("Execution view: /tasks/request/42", FlowExecutionEvidenceFormatter.DescribeOriginal(supported));
    }

    [Fact]
    public void Source_specific_question_does_not_authorize_model_generated_repairs()
    {
        var question = FlowExecutionEvidenceFormatter.InvestigationQuestion(Source(), 17);
        Assert.Contains("investigation only", question);
        Assert.Contains("do not run repairs or reruns", question);
        Assert.Contains("separate configured flow action", question);
        Assert.DoesNotContain("linked monitoring alert", question);
        Assert.False(FlowExecutionEvidenceFormatter.ValidTemplate("{secret}", 128));
    }

    private static FlowExecutionEventDto Source()
    {
        var definition = new FlowExecutionDefinitionDto("42", "Synthetic nightly job", "7", new string('a', 64));
        var target = new FlowExecutionTargetDto(Guid.Parse("d0000000-0000-0000-0000-000000000001"), "Synthetic worker", "worker.example.test");
        var at = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
        return new(Guid.Parse("e0000000-0000-0000-0000-000000000001"), Guid.Parse("f0000000-0000-0000-0000-000000000001"),
            17, FlowExecutionSourceKind.Job, definition, "9007199254740993", "1", target,
            FlowExecutionOutcome.Failed, "Failed", at, at)
        {
            StartedAtUtc = at.AddSeconds(-10), CompletedAtUtc = at, ExitCode = 12, ReasonCode = "job-step-failed",
            Reason = "The synthetic volume is full.", Provenance = "job-terminal-ready",
            Replay = new(FlowExecutionSourceKind.Job, definition, target, "job-run:9007199254740993", "9007199254740993", "1", true)
        };
    }

    private static FlowAiResultDto Ai(FlowExecutionEventDto original) => new(17, RunId, VersionId, NodeId,
        Guid.NewGuid(), Guid.NewGuid(), FlowAiResultStatus.Completed, original.ObservedAtUtc, original.ObservedAtUtc);
}

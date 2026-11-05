using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Components.Pages.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowExecutionEventFactsTests : AsyncBunitContext
{
    public FlowExecutionEventFactsTests() =>
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
    [Fact]
    public void Request_failure_renders_immutable_execution_facts_without_an_alert_or_invented_replay()
    {
        var value = Event();
        var cut = Render<FlowExecutionEventFacts>(p => p.Add(c => c.Event, value));
        var text = cut.Find("[data-testid=flow-execution-facts]").TextContent;
        text.Should().Contain("Original request execution").And.Contain("Synthetic inventory job")
            .And.Contain("Original endpoint").And.Contain("DeliveryUnknown").And.Contain("Unknown")
            .And.Contain("Attempt ID").And.Contain("attempt-2").And.Contain("Source replay unavailable");
        cut.Find("[data-testid=flow-source-execution-id]").TextContent.Should().Be("9223372036854775807");
        text.Should().NotContain("Alert that started").And.NotContain("Rule").And.NotContain("Succeeded");
        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public void Missing_empty_and_available_reference_evidence_remain_distinct_from_read_authority()
    {
        var value = Event();
        var cut = Render<FlowExecutionEventFacts>(p => p.Add(c => c.Event, value));
        cut.Markup.Should().Contain("Result references unavailable");
        cut.Render(p => p.Add(c => c.Event, value with { Results = [] }));
        cut.Markup.Should().Contain("Empty result reference list was recorded");
        cut.Render(p => p.Add(c => c.Event, value with
        {
            Results = [new("request-result", "sample-result-1", Available: true, Truncated: true)]
        }));
        cut.Markup.Should().Contain("Access is checked separately").And.Contain("Source reports available").And.Contain("truncated");
        cut.Markup.Should().NotContain("Authorized output");
        cut.FindAll("a").Should().BeEmpty();
    }

    [Fact]
    public void Original_evidence_is_rendered_as_text_and_unavailable_fields_stay_explicit()
    {
        var value = Event() with { Reason = "<script>synthetic text</script>", ExitCode = null, Replay = null };
        var cut = Render<FlowExecutionEventFacts>(p => p.Add(c => c.Event, value));
        cut.FindAll("script").Should().BeEmpty();
        cut.Find("[data-testid=flow-execution-facts]").TextContent.Should().Contain(value.Reason).And.Contain("Unavailable");
        cut.Markup.Should().Contain("&lt;script&gt;");
    }


    [Fact]
    public void Execution_history_handles_nullable_alert_data_and_explicit_reader_redaction()
    {
        var flow = FlowEditorTests.Definition();
        var source = Event();
        var run = new FlowRunSummaryDto(Guid.NewGuid(), flow.Id, Guid.NewGuid(), source.EventId, source.OccurrenceId,
            FlowRunStatus.WaitingForExecution, source.OccurredAtUtc, null, "execution-pending");
        var detail = new FlowRunDetailDto(run, null, [AiAction(run, source.OccurredAtUtc, FlowAiFailureBehavior.StopFlow)]) { ExecutionEvent = source };
        var cut = Render<FlowHistoryPanel>(p => p.Add(c => c.Flow, flow).Add(c => c.TenantId, 17).Add(c => c.Detail, detail));
        cut.Find("[data-testid=flow-run-result]").TextContent.Should().Contain("Waiting for execution").And.Contain("Original request execution").And.Contain("Follow the configured outcome branch");
        cut.FindComponent<FlowExecutionEventFacts>().Instance.Event.Should().BeSameAs(source);
        cut.Markup.Should().NotContain("Alert that started this run");
        cut.Find("[data-testid=flow-source-execution-id]").TextContent.Should().Be("9223372036854775807");

        cut.Render(p => p.Add(c => c.Flow, flow).Add(c => c.TenantId, 17)
            .Add(c => c.Detail, detail with { ExecutionEvent = null, Trace = null, DataUnavailableReason = "source-read-denied" }));
        cut.FindAll("[data-testid=flow-execution-facts]").Should().BeEmpty();
        cut.Find("[data-testid=flow-source-unavailable]").TextContent.Should().Contain("Original source data is unavailable");
        cut.Find("[data-testid=flow-run-data-unavailable]").TextContent.Should().Contain("source-read-denied");
        cut.Markup.Should().NotContain("9223372036854775807").And.NotContain("No node snapshots were captured");
    }


    [Theory]
    [InlineData(1, FlowAiFailureBehavior.StopFlow, "Stop the flow")]
    [InlineData(1, FlowAiFailureBehavior.ContinueWithAlert, "Continue with the alert")]
    [InlineData(2, FlowAiFailureBehavior.StopFlow, "Follow the configured outcome branch")]
    public void Ai_failure_routing_uses_the_immutable_version_instead_of_the_current_draft(int schema,
        FlowAiFailureBehavior behavior, string expected)
    {
        var original = FlowEditorTests.Definition();
        var graph2 = FlowDraftMigration.UpgradeToSchemaTwo(original.Draft);
        var published = schema == 1 ? original.Draft : graph2;
        var flow = original with { Draft = schema == 1 ? graph2 : original.Draft };
        var time = Event().OccurredAtUtc;
        var version = new FlowVersionDto(Guid.NewGuid(), flow.Id, 17, 1, published, "synthetic-hash", "Synthetic operator", time);
        var run = new FlowRunSummaryDto(Guid.NewGuid(), flow.Id, version.Id, Guid.NewGuid(), Guid.NewGuid(), FlowRunStatus.Failed, time, time);
        var alert = new FlowEventDataDto(Guid.NewGuid(), Guid.NewGuid(), "Recorded monitoring rule", "Synthetic client", "cpu", "CPU", "warning", 95, null, time);
        var detail = new FlowRunDetailDto(run, alert, [AiAction(run, time, behavior)]);
        var cut = Render<FlowHistoryPanel>(p => p.Add(c => c.Flow, flow).Add(c => c.TenantId, 17)
            .Add(c => c.Versions, new[] { version }).Add(c => c.Detail, detail));
        cut.Find("[data-testid=flow-ai-result]").TextContent.Should().Contain("Failure policy: " + expected);
        if (schema == 2) cut.Find("[data-testid=flow-ai-result]").TextContent.Should().NotContain("Stop the flow").And.NotContain("Continue with the alert");
    }

    private static FlowActionSummaryDto AiAction(FlowRunSummaryDto run, DateTimeOffset time, FlowAiFailureBehavior behavior)
    {
        var node = Guid.NewGuid();
        return new(node, "source-investigation", FlowActionStatus.Failed, 1, null, "ai-unsupported", null)
        {
            Kind = FlowActionKind.NetclawInvestigation, Name = "Configured source investigation", AiFailureBehavior = behavior,
            AiResult = new(17, run.Id, run.FlowVersionId, node, Guid.Empty, Guid.NewGuid(), FlowAiResultStatus.Unsupported, time, time)
        };
    }

    private static FlowExecutionEventDto Event()
    {
        var time = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        return new(Guid.NewGuid(), Guid.NewGuid(), 17, FlowExecutionSourceKind.Request,
            new("42", "Synthetic inventory job", "pinned-revision", new string('a', 64)),
            "9223372036854775807", "attempt-2", new(Guid.NewGuid(), "Original endpoint", "sample.invalid"),
            FlowExecutionOutcome.Unknown, "DeliveryUnknown", time, time)
        {
            DefinitionSourceKind = FlowExecutionSourceKind.Job,
            RequestKind = "Job", Reason = "Original transport result was not definitive",
            Replay = new(FlowExecutionSourceKind.Job, new("42", "Synthetic inventory job", "pinned-revision", new string('a', 64)),
                new(null), "job-run:42", "42", "attempt-2", false, "source-material-unavailable")
        };
    }
}

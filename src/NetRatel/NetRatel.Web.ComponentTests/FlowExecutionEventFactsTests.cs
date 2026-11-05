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
        var detail = new FlowRunDetailDto(run, null, []) { ExecutionEvent = source };
        var cut = Render<FlowHistoryPanel>(p => p.Add(c => c.Flow, flow).Add(c => c.TenantId, 17).Add(c => c.Detail, detail));
        cut.Find("[data-testid=flow-run-result]").TextContent.Should().Contain("Waiting for execution").And.Contain("Original request execution");
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

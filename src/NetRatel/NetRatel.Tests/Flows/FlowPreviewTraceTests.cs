using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowPreviewTraceTests
{
    [Fact]
    public void Preview_records_calculated_values_and_stops_at_an_external_action_without_inventing_its_output()
    {
        var graph = FlowTestData.Graph(withCondition: true);
        var input = FlowTestData.EventData("synthetic-client", 94);
        var original = JsonSerializer.Serialize(graph);
        var preview = FlowPureEvaluation.DryRun(new(graph, input));
        var trace = preview.Trace!;
        preview.Valid.Should().BeTrue();
        trace.Provenance.Should().Be(FlowTraceProvenance.Simulated);
        trace.Steps.Should().HaveCount(4);
        trace.Steps.Select(step => step.Sequence).Should().Equal(0, 1, 2, 3);
        trace.Steps[1].Outcome.Should().Be("condition-true");
        trace.Steps[1].Output.Value!.Value.GetBoolean().Should().BeTrue();
        trace.Steps[2].Output.Value!.Value.GetProperty("title").GetString().Should().Contain("synthetic-client");
        trace.Steps[0].NextNodeId.Should().Be(graph.Nodes[1].Id);
        trace.Steps[0].SelectedPort.Should().Be("out");
        trace.Steps[^1].Status.Should().Be(FlowTraceStepStatus.Planned);
        trace.Steps[^1].Output.State.Should().Be(FlowTraceDataState.Missing);
        trace.Steps[^1].SelectedPort.Should().BeNull();
        trace.Steps.Should().OnlyContain(step => step.DurationMilliseconds == null);
        JsonSerializer.Serialize(graph).Should().Be(original);
        JsonSerializer.Serialize(FlowPureEvaluation.DryRun(new(graph, input))).Should().Be(JsonSerializer.Serialize(preview));
    }

    [Fact]
    public void A_false_condition_records_the_actual_decision_and_no_downstream_action()
    {
        var graph = FlowTestData.Graph(withCondition: true);
        var preview = FlowPureEvaluation.DryRun(new(graph, FlowTestData.EventData("synthetic-client", 10)));
        preview.Skipped.Should().BeTrue();
        preview.Trace!.Steps.Should().HaveCount(2);
        var condition = preview.Trace.Steps[^1];
        condition.Status.Should().Be(FlowTraceStepStatus.Skipped);
        condition.Outcome.Should().Be("condition-false");
        condition.Output.Value!.Value.GetBoolean().Should().BeFalse();
        condition.NextNodeId.Should().BeNull();
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public void Ai_preview_does_not_claim_findings_or_advance_a_trace_past_the_missing_result(bool beforeMapping, int steps)
    {
        var graph = FlowTestData.AiGraph(beforeMapping);
        var preview = FlowPureEvaluation.DryRun(new(graph, FlowTestData.EventData("synthetic-client", 94)));
        preview.Valid.Should().BeTrue();
        preview.Code.Should().Be("inert-preview");
        // Legacy clients retain their planned path summary; playback uses only the truthful trace.
        preview.VisitedNodeIds.Should().HaveCount(4);
        preview.Trace!.Steps.Should().HaveCount(steps);
        var last = preview.Trace.Steps[^1];
        last.NodeId.Should().Be(graph.Nodes.Single(node => node.Kind == FlowNodeKind.InvestigateWithAi).Id);
        last.Status.Should().Be(FlowTraceStepStatus.Planned);
        last.Outcome.Should().Be("sample-result-unavailable");
        last.Output.State.Should().Be(FlowTraceDataState.Missing);
        last.Output.Value.Should().BeNull();
        last.NextNodeId.Should().BeNull();
    }

    [Fact]
    public void Large_mapped_output_is_an_explicit_bounded_excerpt_and_never_partial_json()
    {
        var graph = FlowTestData.Graph();
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Kind == FlowNodeKind.MapIncident
            ? node with { Mapping = new("{clientName}", new string('a', 4000) + "{clientName}") } : node).ToArray() };
        var preview = FlowPureEvaluation.DryRun(new(graph, FlowTestData.EventData(new string('c', 128), 94)));
        preview.Valid.Should().BeTrue();
        preview.Trace!.Truncated.Should().BeTrue();
        var output = preview.Trace.Steps.Single(step => step.Outcome == "mapped").Output;
        output.State.Should().Be(FlowTraceDataState.Truncated);
        output.OriginalUtf8Bytes.Should().BeGreaterThan(FlowTraceLimits.MaximumSnapshotBytes);
        output.Value.Should().BeNull();
        output.Excerpt!.Length.Should().BeLessThanOrEqualTo(FlowTraceLimits.MaximumExcerptLength);
        JsonSerializer.SerializeToUtf8Bytes(preview.Trace).Length.Should().BeLessThanOrEqualTo(FlowTraceLimits.MaximumTraceBytes);
    }

    [Fact]
    public void Expanded_mapping_overflow_is_attached_to_the_node_with_no_fabricated_output()
    {
        var graph = FlowTestData.Graph();
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Kind == FlowNodeKind.MapIncident
            ? node with { Mapping = new("{clientName}{clientName}", "original facts") } : node).ToArray() };
        var preview = FlowPureEvaluation.DryRun(new(graph, FlowTestData.EventData(new string('c', 128), 94)));
        preview.Valid.Should().BeFalse();
        preview.Code.Should().Be("mapping-output-limit");
        preview.Trace!.Steps[^1].Status.Should().Be(FlowTraceStepStatus.Failed);
        preview.Trace.Steps[^1].Output.State.Should().Be(FlowTraceDataState.Missing);
        preview.Issues.Should().ContainSingle().Which.NodeId.Should().Be(preview.Trace.Steps[^1].NodeId);
    }

    [Fact]
    public void Trace_is_optional_on_historical_contracts_and_data_states_survive_json_round_trip()
    {
        var result = new FlowDryRunResultDto(true, false, "inert-preview", null, [], []);
        JsonSerializer.Serialize(result).Should().NotContain("Trace");
        foreach (var state in new[] { FlowTraceDataState.Missing, FlowTraceDataState.Null, FlowTraceDataState.Empty, FlowTraceDataState.Truncated })
        {
            var snapshot = new FlowTraceSnapshotDto(state);
            JsonSerializer.Deserialize<FlowTraceSnapshotDto>(JsonSerializer.Serialize(snapshot))!.State.Should().Be(state);
        }
    }
}

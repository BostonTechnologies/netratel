using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowExecutionPreviewTests
{
    [Fact]
    public void Explicit_sample_results_advance_only_the_selected_edges_and_calculate_typed_mapping_and_predicates()
    {
        var graph = Graph();
        var before = JsonSerializer.Serialize(graph);
        var fixture = new FlowPreviewFixtureDto(RepairId, "succeeded")
        {
            Output = JsonSerializer.SerializeToElement(new { outcome = "succeeded", executionId = "9007199254740993", exitCode = 0, summary = "Synthetic repair sample" })
        };
        var request = new FlowExecutionDryRunRequest(graph, Event(), Fixtures: [fixture]);
        var preview = FlowExecutionPreview.DryRun(request, 17);
        preview.Valid.Should().BeTrue(); preview.Code.Should().Be("inert-preview");
        preview.Incident!.Title.Should().Be("Synthetic inventory: 9007199254740993");
        var trace = preview.Trace!;
        trace.Provenance.Should().Be(FlowTraceProvenance.Simulated);
        trace.Steps.Select(step => step.NodeId).Should().Equal(TriggerId, RepairId, ConditionId, MapId, SuccessId);
        trace.Steps[1].Outcome.Should().Be("succeeded");
        trace.Steps[1].Output.Value!.Value.GetProperty("executionId").GetString().Should().Be("9007199254740993");
        trace.Steps[2].SelectedPort.Should().Be("true");
        trace.Steps[2].Output.Value!.Value.GetProperty("result").GetBoolean().Should().BeTrue();
        trace.Steps[0].EdgeId.Should().Be(graph.Edges[0].Id);
        trace.Steps.Should().OnlyContain(step => step.DurationMilliseconds == null);
        JsonSerializer.Serialize(graph).Should().Be(before);
        JsonSerializer.Serialize(FlowExecutionPreview.DryRun(request, 17)).Should().Be(JsonSerializer.Serialize(preview));
    }

    [Fact]
    public void Without_a_fixture_an_external_action_is_planned_and_no_downstream_values_are_invented()
    {
        var preview = FlowExecutionPreview.DryRun(new(Graph(), Event()), 17);
        preview.Valid.Should().BeTrue(); preview.Code.Should().Be("sample-result-unavailable");
        preview.Incident.Should().BeNull();
        preview.Trace!.Steps.Select(step => step.NodeId).Should().Equal(TriggerId, RepairId);
        preview.Trace.Steps[^1].Status.Should().Be(FlowTraceStepStatus.Planned);
        preview.Trace.Steps[^1].Output.State.Should().Be(FlowTraceDataState.Missing);
        preview.Trace.Steps[^1].NextNodeId.Should().BeNull();
    }

    [Fact]
    public void A_fixture_outcome_without_output_does_not_fabricate_zero_exit_code_or_choose_false_for_missing_data()
    {
        var preview = FlowExecutionPreview.DryRun(new(Graph(), Event(), Fixtures: [new(RepairId, "succeeded")]), 17);
        preview.Valid.Should().BeTrue(); preview.Code.Should().Be("node-output-unavailable");
        preview.Trace!.Steps.Should().HaveCount(3);
        preview.Trace.Steps[1].Output.State.Should().Be(FlowTraceDataState.Missing);
        preview.Trace.Steps[^1].Status.Should().Be(FlowTraceStepStatus.Unavailable);
        preview.Trace.Steps[^1].SelectedPort.Should().BeNull();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unsupported")]
    [InlineData("denied")]
    [InlineData("timed_out")]
    public void Non_success_sample_outcomes_follow_their_explicit_named_branch(string outcome)
    {
        var fixture = new FlowPreviewFixtureDto(RepairId, outcome)
        { Output = JsonSerializer.SerializeToElement(new { outcome, executionId = "9007199254740993" }) };
        var preview = FlowExecutionPreview.DryRun(new(Graph(), Event(), Fixtures: [fixture]), 17);
        preview.Trace!.Steps.Select(step => step.NodeId).Should().Equal(TriggerId, RepairId, UnresolvedId);
        preview.Trace.Steps[1].SelectedPort.Should().Be(outcome);
        preview.Trace.Steps[1].NextNodeId.Should().Be(UnresolvedId);
        preview.Trace.Steps[^1].Outcome.Should().Be("finished");
    }

    [Fact]
    public void Source_filters_wrong_tenant_and_fabricated_alert_context_remain_explicit()
    {
        var request = new FlowExecutionDryRunRequest(Graph(), Event());
        var filtered = FlowExecutionPreview.DryRun(request with { Input = Event() with { Outcome = FlowExecutionOutcome.Unknown, RawStatus = "DeliveryUnknown" } }, 17);
        filtered.Skipped.Should().BeTrue(); filtered.Code.Should().Be("event-not-matched");
        filtered.Trace!.Steps.Should().ContainSingle();
        FlowExecutionPreview.DryRun(request, 23).Code.Should().Be("preview-source-invalid");
        FlowExecutionPreview.DryRun(request with { Alert = FlowTestData.EventData("Synthetic client", 12) }, 17).Valid.Should().BeFalse();
    }

    [Fact]
    public void Fixture_null_and_missing_outputs_survive_wire_round_trip_and_invalid_samples_do_not_advance()
    {
        var missing = new FlowPreviewFixtureDto(RepairId, "succeeded");
        var capturedNull = missing with { Output = JsonSerializer.SerializeToElement<object?>(null) };
        JsonSerializer.Deserialize<FlowPreviewFixtureDto>(JsonSerializer.Serialize(missing))!.Output.ValueKind.Should().Be(JsonValueKind.Undefined);
        JsonSerializer.Deserialize<FlowPreviewFixtureDto>(JsonSerializer.Serialize(capturedNull))!.Output.ValueKind.Should().Be(JsonValueKind.Null);
        var invalid = missing with { Output = JsonSerializer.SerializeToElement(new { outcome = "failed" }) };
        FlowExecutionPreview.DryRun(new(Graph(), Event(), Fixtures: [invalid]), 17).Code.Should().Be("preview-fixture-output");
        var numericIdentifier = missing with { Output = JsonSerializer.SerializeToElement(new { executionId = 9007199254740993L }) };
        FlowExecutionPreview.DryRun(new(Graph(), Event(), Fixtures: [numericIdentifier]), 17).Code.Should().Be("preview-fixture-output");
        FlowExecutionPreview.DryRun(new(Graph(), Event(), Fixtures: [missing, missing]), 17).Code.Should().Be("preview-fixture-node");
    }

    [Fact]
    public void Typed_mapping_is_single_pass_and_never_reinterprets_bound_text_as_another_placeholder()
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        { ["text"] = JsonSerializer.SerializeToElement("{definitionName}") };
        var fields = FlowTypedMapping.Map(new("{input.text}", "Original {rawStatus}"), Event(), null, values);
        fields.Title.Should().Be("{definitionName}");
        fields.Description.Should().Be("Original Failed");
        var graph = Graph();
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Kind == FlowNodeKind.MapIncident
            ? node with { Mapping = new("{input.executionId}{input.executionId}", "Original") } : node).ToArray() };
        var sample = new FlowPreviewFixtureDto(RepairId, "succeeded")
        { Output = JsonSerializer.SerializeToElement(new { outcome = "succeeded", exitCode = 0, executionId = new string('9', 128) }) };
        var preview = FlowExecutionPreview.DryRun(new(graph, Event(), Fixtures: [sample]), 17);
        preview.Valid.Should().BeFalse(); preview.Code.Should().Be("mapping-output-limit");
        preview.Trace!.Steps[^1].NodeId.Should().Be(MapId);
        preview.Trace.Steps[^1].Output.State.Should().Be(FlowTraceDataState.Missing);
    }


    [Fact]
    public void A_complete_graph_at_the_v2_node_and_edge_budget_has_a_bounded_deterministic_trace()
    {
        var original = Graph();
        var nodes = new List<FlowNodeDto> { original.Nodes.Single(node => node.Id == TriggerId) };
        for (var index = 0; index < 30; index++)
            nodes.Add(new(Guid.NewGuid(), FlowNodeKind.Condition, "Synthetic condition " + index, new(320 + index * 260, 100))
            { Predicate = new(new(FlowInputSource.Literal, FlowDataType.Boolean, Literal: JsonSerializer.SerializeToElement(true)),
                FlowComparison.Equal, JsonSerializer.SerializeToElement(true)) });
        nodes.Add(new(SuccessId, FlowNodeKind.Finish, "Bounded finish", new(8400, 100)));
        var edges = new List<FlowEdgeDto> { Edge(TriggerId, "event", nodes[1].Id) };
        for (var index = 1; index < nodes.Count - 1; index++)
        {
            edges.Add(Edge(nodes[index].Id, "true", nodes[index + 1].Id));
            edges.Add(Edge(nodes[index].Id, "false", nodes[index + 1].Id));
        }
        var graph = new FlowGraphDto(2, nodes, edges, new());
        graph.Nodes.Should().HaveCount(FlowGraphV2Limits.MaximumNodes);
        graph.Edges.Count.Should().BeLessThanOrEqualTo(FlowGraphV2Limits.MaximumEdges);
        var preview = FlowExecutionPreview.DryRun(new(graph, Event()), 17);
        preview.Valid.Should().BeTrue(); preview.Code.Should().Be("inert-preview");
        preview.Trace!.Steps.Should().HaveCount(FlowGraphV2Limits.MaximumNodes);
        JsonSerializer.SerializeToUtf8Bytes(preview.Trace).Length.Should().BeLessThanOrEqualTo(FlowTraceLimits.MaximumTraceBytes);
    }


    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    public void Ai_findings_are_mapped_only_when_the_sample_explicitly_captured_them(string outcome)
    {
        var original = Graph();
        var source = original.Nodes.Single(node => node.Id == TriggerId);
        var ai = new FlowNodeDto(RepairId, FlowNodeKind.InvestigateWithAi, "Synthetic investigation", new(320, 100),
            ConnectorId: Guid.Parse("91111111-1111-4111-8111-111111111111"), ConnectorRevision: 1)
            { AiSettings = new() };
        var mapping = new FlowNodeDto(MapId, FlowNodeKind.MapIncident, "Map supplied findings", new(620, 100),
            Mapping: new("Investigation: {aiSummary}", "Original {rawStatus}"));
        var finish = original.Nodes.Single(node => node.Id == SuccessId);
        var edges = new List<FlowEdgeDto> { Edge(TriggerId, FlowNodeCatalogue.Event, RepairId), Edge(MapId, FlowNodeCatalogue.Result, SuccessId) };
        edges.AddRange(FlowNodeCatalogue.Find(ai.Kind)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
            .Select(port => Edge(RepairId, port.Id, MapId)));
        var graph = new FlowGraphDto(2, [source, ai, mapping, finish], edges, new());
        FlowGraphV2Validator.ValidateComplete(graph).Valid.Should().BeTrue();
        var missing = new FlowPreviewFixtureDto(RepairId, outcome);
        var unavailable = FlowExecutionPreview.DryRun(new(graph, Event(), Fixtures: [missing]), 17);
        unavailable.Code.Should().Be("mapping-input-unavailable");
        unavailable.Incident.Should().BeNull();
        unavailable.Trace!.Steps[1].Output.State.Should().Be(FlowTraceDataState.Missing);
        unavailable.Trace.Steps[^1].Output.State.Should().Be(FlowTraceDataState.Missing);

        var empty = missing with { Output = JsonSerializer.SerializeToElement(new { summary = "" }) };
        var capturedEmpty = FlowExecutionPreview.DryRun(new(graph, Event(), Fixtures: [empty]), 17);
        capturedEmpty.Code.Should().Be("inert-preview");
        capturedEmpty.Incident!.Title.Should().Be("Investigation: ");
        var supplied = missing with { Output = JsonSerializer.SerializeToElement(new { summary = "Explicit synthetic findings" }) };
        var captured = FlowExecutionPreview.DryRun(new(graph, Event(), Fixtures: [supplied]), 17);
        captured.Incident!.Title.Should().Be("Investigation: Explicit synthetic findings");
        captured.Trace!.Steps[1].Output.Value!.Value.GetProperty("summary").GetString().Should().Be("Explicit synthetic findings");
    }

    internal static readonly Guid TriggerId = Guid.Parse("f05d4ba5-a3b3-4815-8d59-78d071e45c01");
    internal static readonly Guid RepairId = Guid.Parse("f05d4ba5-a3b3-4815-8d59-78d071e45c02");
    internal static readonly Guid ConditionId = Guid.Parse("f05d4ba5-a3b3-4815-8d59-78d071e45c03");
    internal static readonly Guid MapId = Guid.Parse("f05d4ba5-a3b3-4815-8d59-78d071e45c04");
    internal static readonly Guid SuccessId = Guid.Parse("f05d4ba5-a3b3-4815-8d59-78d071e45c05");
    internal static readonly Guid UnresolvedId = Guid.Parse("f05d4ba5-a3b3-4815-8d59-78d071e45c06");
    internal static FlowGraphDto Graph()
    {
        var agent = Guid.Parse("bf8b34e2-556f-4337-aeca-b82075675f52");
        FlowNodeDto[] nodes =
        [
            new(TriggerId, FlowNodeKind.ExecutionEvent, "Job failure", new(20, 100))
            { ExecutionTrigger = new([FlowExecutionSourceKind.Job], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) },
            new(RepairId, FlowNodeKind.RunLibraryScript, "Pinned synthetic repair", new(320, 100))
            { ExecutionAction = new(FlowExecutionSourceKind.Script, new("42", "Synthetic repair", "7", new string('a', 64)))
                { Target = new(agent, "Synthetic endpoint"), Parameters = [] } },
            new(ConditionId, FlowNodeKind.Condition, "Successful exit", new(620, 100))
            { Predicate = new(new(FlowInputSource.NodeOutput, FlowDataType.Number, "exitCode", RepairId), FlowComparison.Equal, JsonSerializer.SerializeToElement(0)) },
            new(MapId, FlowNodeKind.MapIncident, "Calculate mapped fields", new(920, 100), Mapping: new("{definitionName}: {input.executionId}", "Original {rawStatus} on {targetName}"))
            { Inputs = new Dictionary<string, FlowInputBindingDto> { ["executionId"] = new(FlowInputSource.NodeOutput, FlowDataType.Text, "executionId", RepairId) } },
            new(SuccessId, FlowNodeKind.Finish, "Sample successful path", new(1220, 100)),
            new(UnresolvedId, FlowNodeKind.Finish, "Explicit unresolved path", new(920, 400))
        ];
        var edges = new List<FlowEdgeDto>
        {
            Edge(TriggerId, "event", RepairId), Edge(RepairId, "succeeded", ConditionId),
            Edge(ConditionId, "true", MapId), Edge(ConditionId, "false", UnresolvedId), Edge(MapId, "result", SuccessId)
        };
        foreach (var port in new[] { "failed", "timed_out", "cancelled", "unknown", "unsupported", "denied" })
            edges.Add(Edge(RepairId, port, UnresolvedId));
        return new(2, nodes, edges, new());
    }
    internal static FlowExecutionEventDto Event()
    {
        var time = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        return new(Guid.Parse("d2cdc813-051c-4833-80aa-12210e995294"), Guid.Parse("24e65abc-6a46-47ca-8907-444dca7edd0c"), 17,
            FlowExecutionSourceKind.Job, new("12", "Synthetic inventory", "7", new string('b', 64)), "9007199254740991", "attempt-1",
            new(Guid.Parse("bf8b34e2-556f-4337-aeca-b82075675f52"), "Synthetic endpoint"),
            FlowExecutionOutcome.Failed, "Failed", time, time) { Reason = "Synthetic disk quota failure", ExitCode = 1 };
    }
    private static FlowEdgeDto Edge(Guid source, string port, Guid target) => new(source, port, target, "in") { Id = Guid.NewGuid() };
}

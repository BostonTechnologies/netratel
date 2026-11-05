using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Flows;
using Xunit;
using static NetRatel.Tests.Flows.FlowExecutionPreviewTests;

namespace NetRatel.Tests.Flows;

public sealed class FlowExecutionPreviewSecretFixtureTests
{
    private const string SecretReference = "SYNTHETIC_FLOW_TOKEN";

    [Theory]
    [InlineData(FlowNodeKind.RunLibraryScript)]
    [InlineData(FlowNodeKind.RunJob)]
    public void Explicit_action_fixture_continues_with_redacted_secret_input_and_real_downstream_pure_values(FlowNodeKind kind)
    {
        var graph = WithSecret(kind);
        FlowGraphV2Validator.ValidateComplete(graph).Valid.Should().BeTrue();
        var before = JsonSerializer.Serialize(graph);
        var preview = FlowExecutionPreview.DryRun(new(graph, Event(), Fixtures: [Fixture()]), 17);

        preview.Valid.Should().BeTrue();
        preview.Code.Should().Be("inert-preview");
        preview.Incident!.Title.Should().Be("Synthetic inventory: 9007199254740993");
        preview.Trace!.Steps.Select(step => step.NodeId).Should().Equal(TriggerId, RepairId, ConditionId, MapId, SuccessId);
        var action = preview.Trace.Steps[1];
        action.Provenance.Should().Be(FlowTraceProvenance.Simulated);
        action.SelectedPort.Should().Be(FlowNodeCatalogue.Succeeded);
        var input = action.Input.Value!.Value;
        input.GetProperty("inputs").EnumerateObject().Select(property => property.Name).Should().Equal("message");
        input.GetProperty("inputs").GetProperty("message").GetString().Should().Be("Explicit non-secret input");
        AssertRedacted(input);
        preview.Trace.Steps[2].Output.Value!.Value.GetProperty("result").GetBoolean().Should().BeTrue();
        JsonSerializer.Serialize(preview).Should().NotContain(SecretReference).And.NotContain("credential");
        JsonSerializer.Serialize(graph).Should().Be(before);
        var binding = graph.Nodes.Single(node => node.Id == RepairId).Inputs!["credential"];
        FlowValueEvaluation.Resolve(binding, Event(), null, new Dictionary<Guid, JsonElement>()).Code
            .Should().Be("secret-reference-unavailable");
    }

    [Theory]
    [InlineData(FlowNodeKind.RunLibraryScript)]
    [InlineData(FlowNodeKind.RunJob)]
    public void An_action_without_an_explicit_fixture_still_stops_at_its_unavailable_secret_input(FlowNodeKind kind)
    {
        var graph = WithSecret(kind);
        FlowGraphV2Validator.ValidateComplete(graph).Valid.Should().BeTrue();
        var preview = FlowExecutionPreview.DryRun(new(graph, Event()), 17);

        preview.Code.Should().Be("secret-reference-unavailable");
        preview.Incident.Should().BeNull();
        preview.Trace!.Steps.Select(step => step.NodeId).Should().Equal(TriggerId, RepairId);
        preview.Trace.Steps[^1].Status.Should().Be(FlowTraceStepStatus.Unavailable);
        preview.Trace.Steps[^1].Output.State.Should().Be(FlowTraceDataState.Missing);
        preview.Trace.Steps[^1].NextNodeId.Should().BeNull();
        JsonSerializer.Serialize(preview).Should().NotContain(SecretReference).And.NotContain("credential");
    }

    [Fact]
    public void An_explicit_fixture_does_not_bypass_an_ordinary_missing_input()
    {
        var graph = WithSecret(FlowNodeKind.RunLibraryScript);
        var producerId = Guid.NewGuid();
        var repair = graph.Nodes.Single(node => node.Id == RepairId);
        var producer = repair with
        {
            Id = producerId, Name = "Explicit sample producer",
            ExecutionAction = repair.ExecutionAction! with { Parameters = [] }, Inputs = null
        };
        var inputs = repair.Inputs!.ToDictionary(pair => pair.Key, pair => pair.Value);
        inputs["message"] = new(FlowInputSource.NodeOutput, FlowDataType.Text, "summary", producerId);
        var nodes = graph.Nodes.Select(node => node.Id == RepairId ? node with { Inputs = inputs } : node).Append(producer).ToArray();
        var edges = graph.Edges.Select(edge => edge.SourceNodeId == TriggerId ? edge with { TargetNodeId = producerId } : edge).ToList();
        edges.AddRange(FlowNodeCatalogue.Find(producer.Kind)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
            .Select(port => new FlowEdgeDto(producerId, port.Id, RepairId, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }));
        graph = graph with { Nodes = nodes, Edges = edges };
        FlowGraphV2Validator.ValidateComplete(graph).Valid.Should().BeTrue();

        var preview = FlowExecutionPreview.DryRun(new(graph, Event(), Fixtures: [new(producerId, "succeeded"), Fixture()]), 17);
        preview.Code.Should().Be("node-output-unavailable");
        preview.Incident.Should().BeNull();
        preview.Trace!.Steps.Select(step => step.NodeId).Should().Equal(TriggerId, producerId, RepairId);
        var blocked = preview.Trace.Steps[^1];
        blocked.Status.Should().Be(FlowTraceStepStatus.Unavailable);
        blocked.SelectedPort.Should().BeNull();
        blocked.Output.State.Should().Be(FlowTraceDataState.Missing);
        AssertRedacted(blocked.Input.Value!.Value);
        JsonSerializer.Serialize(preview).Should().NotContain(SecretReference).And.NotContain("credential");
    }

    [Fact]
    public void Pure_mapping_does_not_gain_a_secret_value_from_an_earlier_explicit_action_fixture()
    {
        var graph = WithSecret(FlowNodeKind.RunLibraryScript);
        graph = graph with
        {
            Nodes = graph.Nodes.Select(node => node.Id == MapId
                ? node with { Inputs = node.Inputs!.ToDictionary(pair => pair.Key, pair => pair.Value)
                    .Append(new KeyValuePair<string, FlowInputBindingDto>("credential", Secret())).ToDictionary(pair => pair.Key, pair => pair.Value) }
                : node).ToArray()
        };
        FlowGraphV2Validator.ValidateComplete(graph).Valid.Should().BeTrue();
        var preview = FlowExecutionPreview.DryRun(new(graph, Event(), Fixtures: [Fixture()]), 17);

        preview.Code.Should().Be("secret-reference-unavailable");
        preview.Incident.Should().BeNull();
        preview.Trace!.Steps.Select(step => step.NodeId).Should().Equal(TriggerId, RepairId, ConditionId, MapId);
        preview.Trace.Steps[^1].Status.Should().Be(FlowTraceStepStatus.Unavailable);
        preview.Trace.Steps[^1].Output.State.Should().Be(FlowTraceDataState.Missing);
        preview.Trace.Steps[^1].NextNodeId.Should().BeNull();
        preview.Trace.Steps[^1].Input.Value!.Value.TryGetProperty("unavailableInputs", out _).Should().BeFalse();
        JsonSerializer.Serialize(preview).Should().NotContain(SecretReference).And.NotContain("credential");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Secret_redaction_does_not_turn_missing_or_null_fixture_output_into_a_condition_value(bool capturedNull)
    {
        var fixture = new FlowPreviewFixtureDto(RepairId, "succeeded");
        if (capturedNull) fixture = fixture with { Output = JsonSerializer.SerializeToElement<object?>(null) };
        var preview = FlowExecutionPreview.DryRun(new(WithSecret(FlowNodeKind.RunLibraryScript), Event(), Fixtures: [fixture]), 17);

        preview.Code.Should().Be("node-output-unavailable");
        preview.Incident.Should().BeNull();
        preview.Trace!.Steps.Select(step => step.NodeId).Should().Equal(TriggerId, RepairId, ConditionId);
        preview.Trace.Steps[1].Output.State.Should().Be(capturedNull ? FlowTraceDataState.Null : FlowTraceDataState.Missing);
        AssertRedacted(preview.Trace.Steps[1].Input.Value!.Value);
        preview.Trace.Steps[^1].SelectedPort.Should().BeNull();
        JsonSerializer.Serialize(preview).Should().NotContain(SecretReference).And.NotContain("credential");
    }

    private static void AssertRedacted(JsonElement input)
    {
        var marker = input.GetProperty("unavailableInputs");
        marker.GetProperty("state").GetString().Should().Be("unavailable");
        marker.GetProperty("code").GetString().Should().Be("secret-reference-unavailable");
        marker.GetProperty("redacted").GetBoolean().Should().BeTrue();
        marker.GetProperty("count").GetInt32().Should().Be(1);
        marker.EnumerateObject().Select(property => property.Name).Should().Equal("state", "code", "redacted", "count");
    }

    private static FlowInputBindingDto Secret() => new(FlowInputSource.SecretReference, FlowDataType.SecretReference, SecretReference);

    private static FlowPreviewFixtureDto Fixture() => new(RepairId, "succeeded")
    {
        Output = JsonSerializer.SerializeToElement(new { outcome = "succeeded", executionId = "9007199254740993", exitCode = 0 })
    };

    private static FlowGraphDto WithSecret(FlowNodeKind kind)
    {
        var graph = Graph();
        return graph with
        {
            Nodes = graph.Nodes.Select(node => node.Id == RepairId ? node with
            {
                Kind = kind,
                ExecutionAction = node.ExecutionAction! with
                {
                    SourceKind = kind == FlowNodeKind.RunJob ? FlowExecutionSourceKind.Job : FlowExecutionSourceKind.Script,
                    Parameters = [new("credential", FlowDataType.SecretReference, true), new("message", FlowDataType.Text, true)]
                },
                Inputs = new Dictionary<string, FlowInputBindingDto>
                {
                    ["credential"] = Secret(),
                    ["message"] = new(FlowInputSource.Literal, FlowDataType.Text, Literal: JsonSerializer.SerializeToElement("Explicit non-secret input"))
                }
            } : node).ToArray()
        };
    }
}

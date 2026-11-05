using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Services.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowCanvasAdapterTests
{
    [Fact]
    public async Task Native_Commands_Restore_All_Typed_Edges_Properties_And_Geometry_Without_Execution()
    {
        var graph = FlowGraphTemplates.IncidentFromAlert() with { Viewport = new(.9090909090909091, 140, 96, 0, 0, 960, 600) };
        var mapping = graph.Nodes.Single(n => n.Kind == FlowNodeKind.MapIncident);
        graph = graph with { Nodes = graph.Nodes.Select(n => n.Id == mapping.Id ? n with { Position = new(425, 210), Mapping = n.Mapping! with { TitleTemplate = "Alert {ruleName}", Priority = 2 } } : n).ToArray() };
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var adapter = new FlowCanvasAdapter();
            await adapter.RestoreAsync(graph);
            var captured = adapter.Capture(graph.Viewport.ScrollX, graph.Viewport.ScrollY);
            Assert.Equal(JsonSerializer.Serialize(graph), JsonSerializer.Serialize(captured));
            Assert.Equal(2, adapter.Tree.Links.Count);
            Assert.Equal(0, adapter.ExecutionCalls);
            Assert.DoesNotContain("$type", JsonSerializer.Serialize(captured), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(2)]
    public async Task Insert_At_Current_Zoom_Preserves_Canonical_Position_Through_Save_And_Restore(double scale)
    {
        await using var adapter = new FlowCanvasAdapter();
        await adapter.RestoreAsync(new(FlowLimits.SchemaVersion, [], [], new(scale, 110, 90, -40, -30, 800, 600)));
        var node = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Condition, "Inserted condition", new(235.5, -17.25), new(FlowValueField.NumericValue, FlowComparison.GreaterThan, 5));
        var native = await adapter.AddAsync(node);
        Assert.Equal(220 / scale, native.Size.Width); Assert.Equal(140 / scale, native.Size.Height);
        var captured = adapter.Capture(110, 90);
        Assert.Equal(node.Position, Assert.Single(captured.Nodes).Position);
        await using var restored = new FlowCanvasAdapter();
        await restored.RestoreAsync(captured);
        Assert.Equal(JsonSerializer.Serialize(captured), JsonSerializer.Serialize(restored.Capture(110, 90)));
        Assert.Equal(0, adapter.ExecutionCalls);
    }

    [Fact]
    public async Task Connected_Node_Deletion_Removes_Both_Incident_Native_Links()
    {
        var graph = FlowGraphTemplates.IncidentFromAlert();
        await using var adapter = new FlowCanvasAdapter();
        await adapter.RestoreAsync(graph);
        Assert.Equal(2, adapter.Tree.Links.Count);
        await adapter.RemoveAsync(graph.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident).Id);
        Assert.Empty(adapter.Tree.Links);
        Assert.Empty(adapter.Capture().Edges);
        Assert.Equal(2, adapter.Capture().Nodes.Count);
        Assert.Equal(0, adapter.ExecutionCalls);
    }

    [Fact]
    public async Task Ai_Output_Restores_Both_Sequential_And_Historical_Ai_Only_Paths_Without_Execution()
    {
        var original = FlowGraphTemplates.IncidentFromAlert();
        var map = original.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident);
        var incident = original.Nodes.Single(node => node.Kind == FlowNodeKind.CreateIncident);
        var ai = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.InvestigateWithAi, "AI investigation", new(490, 280), ConnectorId: Guid.NewGuid())
            { AiSettings = new(9, FlowAiFailureBehavior.StopFlow) };
        var chain = original with { Nodes = [.. original.Nodes, ai], Edges = [original.Edges[0],
            new(map.Id, FlowLimits.OutputPort, ai.Id, FlowLimits.InputPort), new(ai.Id, FlowLimits.OutputPort, incident.Id, FlowLimits.InputPort)] };
        await using var adapter = new FlowCanvasAdapter();
        await adapter.RestoreAsync(chain);
        Assert.Contains(adapter.Ports.Values, port => port == (ai.Id, true));
        Assert.Equal(JsonSerializer.Serialize(chain), JsonSerializer.Serialize(adapter.Capture()));
        Assert.Equal("alert + AI result", FlowCanvasAdapter.OutputType(ai.Kind));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ConnectAsync(ai.Id, map.Id));
        Assert.Equal(0, adapter.ExecutionCalls);

        var aiOnly = chain with { Nodes = chain.Nodes.Where(node => node.Id != incident.Id).ToArray(),
            Edges = chain.Edges.Where(edge => edge.TargetNodeId != incident.Id).ToArray() };
        await using var historical = new FlowCanvasAdapter();
        await historical.RestoreAsync(aiOnly);
        Assert.Equal(JsonSerializer.Serialize(aiOnly), JsonSerializer.Serialize(historical.Capture()));
        Assert.Equal(0, historical.ExecutionCalls);
    }

    [Fact]
    public async Task Keyboard_Connection_Path_Uses_The_Same_Typed_Native_Commands_And_Rejects_Unsafe_Connections()
    {
        var graph = FlowGraphTemplates.IncidentFromAlert();
        await using var adapter = new FlowCanvasAdapter();
        await adapter.RestoreAsync(graph with { Edges = [] });
        var trigger = graph.Nodes.Single(n => n.Kind == FlowNodeKind.AlertRaised).Id;
        var mapping = graph.Nodes.Single(n => n.Kind == FlowNodeKind.MapIncident).Id;
        var action = graph.Nodes.Single(n => n.Kind == FlowNodeKind.CreateIncident).Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ConnectAsync(trigger, action));
        await adapter.ConnectAsync(trigger, mapping);
        await adapter.ConnectAsync(mapping, action);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ConnectAsync(trigger, mapping));
        Assert.Equal(2, adapter.Capture().Edges.Count);
        await adapter.DisconnectAsync(adapter.Capture().Edges[0]);
        Assert.Single(adapter.Capture().Edges);
        await adapter.RemoveAsync(mapping);
        Assert.Empty(adapter.Capture().Edges);
        Assert.Equal(0, adapter.ExecutionCalls);
    }
    [Fact]
    public async Task Schema_Two_Restores_Named_Ports_Routes_And_Optional_Inputs_Without_Normalizing_History()
    {
        var graph = FlowDraftMigration.UpgradeToSchemaTwo(FlowGraphTemplates.IncidentFromAlert());
        var mapping = graph.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident);
        var script = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RunLibraryScript, "Diagnostic library action", new(120, 400))
        {
            ExecutionAction = new(FlowExecutionSourceKind.Script, new("9007199254740993", "Diagnostic library action", "3", new string('a', 64)))
            { Target = new(Guid.NewGuid(), "Synthetic endpoint"), Parameters = [] }
        };
        graph = graph with
        {
            Nodes = [.. graph.Nodes.Select(node => node.Id == mapping.Id ? node with
            { Inputs = new Dictionary<string, FlowInputBindingDto> { ["client"] = new(FlowInputSource.Event, FlowDataType.Text, "alert.clientName") } } : node), script],
            Edges = graph.Edges.Select((edge, index) => index == 0 ? edge with
            { Route = new([new(240, -30), new(290, 80)]) } : edge).ToArray(),
            Viewport = new(.5, 120, 90, -20, -10, 900, 700)
        };
        Assert.True(FlowGraphV2Validator.ValidateDraft(graph).Valid);
        await using var adapter = new FlowCanvasAdapter();
        await adapter.RestoreAsync(graph);
        Assert.Equal(2, adapter.SchemaVersion);
        Assert.Equal(JsonSerializer.Serialize(graph), JsonSerializer.Serialize(adapter.Capture(120, 90)));
        Assert.Equal(7, adapter.Ports.Count(port => port.Value.NodeId == script.Id && port.Value.Output));
        Assert.Equal("9007199254740993", adapter.Capture().Nodes.Single(node => node.Id == script.Id).ExecutionAction!.Definition.Id);
        Assert.Equal(0, adapter.ExecutionCalls);
    }

    [Fact]
    public async Task Exclusive_Outcome_Convergence_Uses_Real_Native_Links_And_Bounded_Routes()
    {
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.AlertRaised, "Synthetic alert", new(20, 80));
        var condition = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Condition, "Threshold", new(340, 80),
            new(FlowValueField.NumericValue, FlowComparison.GreaterThan, 90));
        var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Finish", new(660, 80));
        var graph = new FlowGraphDto(2, [trigger, condition, finish],
        [
            new(trigger.Id, FlowNodeCatalogue.Event, condition.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
            new(condition.Id, FlowNodeCatalogue.True, finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
            new(condition.Id, FlowNodeCatalogue.False, finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }
        ], new());
        Assert.True(FlowGraphV2Validator.ValidateComplete(graph).Valid);
        await using var adapter = new FlowCanvasAdapter();
        await adapter.RestoreAsync(graph);
        Assert.Equal(3, adapter.Tree.Links.Count);
        Assert.Empty(adapter.CompatibleInputs(condition.Id, FlowNodeCatalogue.True));
        Assert.Empty(adapter.CompatibleInputs(condition.Id, FlowNodeCatalogue.False));
        Assert.Equal(JsonSerializer.Serialize(graph), JsonSerializer.Serialize(adapter.Capture()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ConnectAsync(condition.Id, finish.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ConnectAsync(condition.Id, FlowNodeCatalogue.True, finish.Id, FlowNodeCatalogue.Input));
        var edgeId = graph.Edges[0].Id!.Value;
        adapter.SetRoute(edgeId, [new(180, 30)]);
        var routed = adapter.Capture();
        Assert.Equal(new FlowPositionDto(180, 30), Assert.Single(routed.Edges.Single(edge => edge.Id == edgeId).Route!.Points));
        await using var restored = new FlowCanvasAdapter();
        await restored.RestoreAsync(routed);
        Assert.Equal(JsonSerializer.Serialize(routed), JsonSerializer.Serialize(restored.Capture()));
        Assert.Throws<InvalidOperationException>(() => adapter.SetRoute(edgeId, Enumerable.Range(0, 9).Select(index => new FlowPositionDto(index, index)).ToArray()));
        Assert.Throws<InvalidOperationException>(() => adapter.SetRoute(edgeId, [new(double.NaN, 0)]));
        Assert.Equal(JsonSerializer.Serialize(routed), JsonSerializer.Serialize(adapter.Capture()));
        adapter.SetRoute(edgeId, null);
        Assert.Null(adapter.Capture().Edges.Single(edge => edge.Id == edgeId).Route);
        Assert.Equal(0, adapter.ExecutionCalls);
    }

    [Fact]
    public async Task Saved_Routes_Never_Implicitly_Upgrade_A_Schema_One_Or_Unknown_Graph()
    {
        var historical = FlowGraphTemplates.IncidentFromAlert();
        await using var adapter = new FlowCanvasAdapter();
        await adapter.RestoreAsync(historical);
        Assert.Throws<InvalidOperationException>(() => adapter.SetRoute(Guid.NewGuid(), [new(200, 50)]));
        Assert.Equal(JsonSerializer.Serialize(historical), JsonSerializer.Serialize(adapter.Capture()));
        await using var unknown = new FlowCanvasAdapter();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unknown.RestoreAsync(historical with { SchemaVersion = 99 }));
        await using var mismatched = new FlowCanvasAdapter();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mismatched.RestoreAsync(historical with
        { Edges = historical.Edges.Select(edge => edge with { Route = new([new(200, 50)]) }).ToArray() }));
        Assert.Equal(0, adapter.ExecutionCalls);
    }

}

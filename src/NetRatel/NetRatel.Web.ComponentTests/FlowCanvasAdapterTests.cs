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
}

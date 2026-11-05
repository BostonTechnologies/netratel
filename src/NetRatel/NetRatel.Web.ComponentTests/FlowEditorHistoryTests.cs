using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Services.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowEditorHistoryTests
{
    [Fact]
    public void Layout_Gesture_Coalesces_And_Viewport_Navigation_Does_Not_Consume_History()
    {
        var graph = FlowGraphTemplates.IncidentFromAlert();
        var selected = graph.Nodes[1].Id;
        var history = new FlowEditorHistory(); history.Reset("Original", graph, selected);
        var moved = graph;
        for (var position = 1; position <= 40; position++)
        {
            moved = graph with { Nodes = graph.Nodes.Select(node => node.Id == selected ? node with { Position = new(position, 80) } : node).ToArray() };
            history.Record("Original", moved, selected, "drag-one");
        }
        Assert.Equal(2, history.Count);
        history.Record("Original", moved with { Viewport = new(.5, 450, 300, -40, -20, 900, 700) }, selected);
        Assert.Equal(2, history.Count);
        var undone = history.Undo(FlowCanvasAdapter.StructurallySafe)!;
        Assert.Equal(JsonSerializer.Serialize(graph), JsonSerializer.Serialize(undone.Graph));
        Assert.Equal(selected, undone.SelectedNodeId);
        var redone = history.Redo(FlowCanvasAdapter.StructurallySafe)!;
        Assert.Equal(JsonSerializer.Serialize(moved), JsonSerializer.Serialize(redone.Graph));
        Assert.Equal(0, redone.Graph.Viewport.ScrollX);
    }

    [Fact]
    public void New_Edit_Clears_Redo_And_History_Is_Bounded()
    {
        var graph = FlowGraphTemplates.IncidentFromAlert();
        var history = new FlowEditorHistory(); history.Reset("Initial", graph, null);
        for (var index = 0; index < 60; index++) history.Record("Name " + index, graph, null);
        Assert.Equal(FlowEditorHistory.MaximumEntries, history.Count);
        Assert.True(history.CanUndo);
        Assert.Equal("Name 58", history.Undo(_ => true)!.Name);
        Assert.True(history.CanRedo);
        history.Record("Replacement", graph, null);
        Assert.False(history.CanRedo);
        Assert.Equal("Name 58", history.Undo(_ => true)!.Name);
        Assert.Equal("Replacement", history.Redo(_ => true)!.Name);
    }

    [Fact]
    public void Incomplete_Invalid_Form_State_Does_Not_Restore_An_Unsafe_Canvas()
    {
        var graph = FlowGraphTemplates.IncidentFromAlert();
        var history = new FlowEditorHistory(); history.Reset("Initial", graph, null);
        var unsafeGraph = graph with { Nodes = graph.Nodes.Select((node, index) => index == 0 ? node with { Position = new(double.MaxValue, 0) } : node).ToArray() };
        history.Record("Incomplete", unsafeGraph, null);
        history.Record("Complete", graph, graph.Nodes[0].Id);
        Assert.Equal("Initial", history.Undo(FlowCanvasAdapter.StructurallySafe)!.Name);
        Assert.Equal("Complete", history.Redo(FlowCanvasAdapter.StructurallySafe)!.Name);
        history.Clear(); Assert.Equal(0, history.Count); Assert.False(history.CanUndo); Assert.False(history.CanRedo);
    }

    [Fact]
    public void Name_With_Newline_And_Edited_Collection_Are_Snapshotted_Without_Mutable_References()
    {
        var original = FlowGraphTemplates.IncidentFromAlert();
        var nodes = original.Nodes.ToArray();
        var graph = original with { Nodes = nodes };
        var history = new FlowEditorHistory(); history.Reset("A\nB", graph, nodes[0].Id);
        nodes[0] = nodes[0] with { Name = "Later mutation" };
        history.Record("Next", graph, null);
        var undone = history.Undo(FlowCanvasAdapter.StructurallySafe)!;
        Assert.Equal("A\nB", undone.Name);
        Assert.NotEqual("Later mutation", undone.Graph.Nodes[0].Name);
    }
}

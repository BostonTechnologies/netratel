using NetRatel.Shared.Contracts.Flows;
using VeloxDev.MVVM;
using VeloxDev.WorkflowSystem;

namespace NetRatel.Web.Services.Flows;

/// <summary>Canonical NetRatel DTOs are translated to reviewed Core8 models. The editor cannot execute nodes.</summary>
public sealed class FlowCanvasAdapter : IAsyncDisposable
{
    public event Action? Changed;
    public TreeDefaultViewModel Tree { get; } = new();
    public Dictionary<IWorkflowNodeViewModel, FlowNodeDto> Metadata { get; } = new();
    public Dictionary<IWorkflowSlotViewModel, (Guid NodeId, bool Output)> Ports { get; } = new();
    public int ExecutionCalls { get; private set; }

    public FlowCanvasAdapter() => Tree.SetHelper(new TypedTreeHelper(this));

    public async Task<IWorkflowNodeViewModel> AddAsync(FlowNodeDto definition, CancellationToken cancellationToken = default)
    {
        if (Tree.Nodes.Count >= FlowLimits.MaximumNodes || Metadata.Values.Any(n => n.Id == definition.Id))
            throw new InvalidOperationException("Node limit or duplicate node ID.");
        var node = new NodeDefaultViewModel();
        node.SetHelper(new InertNodeHelper(this));
        Metadata.Add(node, definition);
        await CompleteAsync(Tree.CreateNodeCommand, node, cancellationToken);
        await CompleteAsync(node.SetAnchorCommand, new Anchor(definition.Position.X, definition.Position.Y), cancellationToken);
        await CompleteAsync(node.SetSizeCommand, new Size(220, 140), cancellationToken);
        if (definition.Kind != FlowNodeKind.AlertRaised) await AddPortAsync(node, definition.Id, false, cancellationToken);
        if (definition.Kind != FlowNodeKind.CreateIncident) await AddPortAsync(node, definition.Id, true, cancellationToken);
        return node;
    }

    private async Task AddPortAsync(IWorkflowNodeViewModel node, Guid id, bool output, CancellationToken token)
    {
        var slot = new SlotDefaultViewModel();
        Ports.Add(slot, (id, output));
        await CompleteAsync(node.CreateSlotCommand, slot, token);
        await CompleteAsync(slot.SetChannelCommand, output ? SlotChannel.OneTarget : SlotChannel.OneSource, token);
    }

    public async Task ConnectAsync(Guid source, Guid target, CancellationToken token = default)
    {
        var from = Ports.SingleOrDefault(p => p.Value == (source, true)).Key;
        var to = Ports.SingleOrDefault(p => p.Value == (target, false)).Key;
        if (from is null || to is null || !CanConnect(from, to)) throw new InvalidOperationException("Choose compatible unused ports on an acyclic path.");
        await CompleteAsync(Tree.SendConnectionCommand, from, token);
        try { await CompleteAsync(Tree.ReceiveConnectionCommand, to, token); }
        finally { await CompleteAsync(Tree.ResetVirtualLinkCommand, null, token); }
        Changed?.Invoke();
    }

    public async Task RemoveAsync(Guid id, CancellationToken token = default)
    {
        var node = Metadata.Single(p => p.Value.Id == id).Key;
        await CompleteAsync(node.DeleteCommand, null, token);
        foreach (var slot in Ports.Where(p => p.Value.NodeId == id).Select(p => p.Key).ToArray()) Ports.Remove(slot);
        Metadata.Remove(node);
        Changed?.Invoke();
    }

    public async Task DisconnectAsync(FlowEdgeDto edge, CancellationToken token = default)
    {
        var link = Tree.Links.Single(l => Ports[l.Sender].NodeId == edge.SourceNodeId && Ports[l.Receiver].NodeId == edge.TargetNodeId);
        await CompleteAsync(link.DeleteCommand, null, token);
        Changed?.Invoke();
    }

    public void Update(FlowNodeDto definition)
    {
        var node = Metadata.Single(p => p.Value.Id == definition.Id).Key;
        Metadata[node] = definition;
        Changed?.Invoke();
    }

    public FlowGraphDto Capture(double scrollX = 0, double scrollY = 0)
    {
        var layout = Tree.Layout;
        var scale = layout.Scale.Horizontal;
        var nodes = Tree.Nodes.Select(node => Metadata[node] with
        { Position = new(Math.Round(node.Anchor.Horizontal * scale, 6), Math.Round(node.Anchor.Vertical * scale, 6)) }).ToArray();
        var edges = Tree.Links.Where(l => Ports.ContainsKey(l.Sender) && Ports.ContainsKey(l.Receiver))
            .Select(l => new FlowEdgeDto(Ports[l.Sender].NodeId, FlowLimits.OutputPort, Ports[l.Receiver].NodeId, FlowLimits.InputPort)).ToArray();
        return new(FlowLimits.SchemaVersion, nodes, edges, new(scale, scrollX, scrollY,
            layout.NegativeOffset.Horizontal, layout.NegativeOffset.Vertical, layout.PositiveOffset.Horizontal, layout.PositiveOffset.Vertical));
    }

    public async Task RestoreAsync(FlowGraphDto graph, CancellationToken token = default)
    {
        if (!StructurallySafe(graph)) throw new InvalidOperationException("Unsupported or unsafe draft geometry/connections.");
        foreach (var node in graph.Nodes) await AddAsync(node, token);
        Tree.Layout.NegativeOffset = new Offset(graph.Viewport.NegativeX, graph.Viewport.NegativeY);
        Tree.Layout.PositiveOffset = new Offset(graph.Viewport.PositiveX, graph.Viewport.PositiveY);
        foreach (var edge in graph.Edges) await ConnectAsync(edge.SourceNodeId, edge.TargetNodeId, token);
        Tree.Layout.Scale = new Scale(graph.Viewport.Scale, graph.Viewport.Scale);
    }

    private bool CanConnect(IWorkflowSlotViewModel sender, IWorkflowSlotViewModel receiver)
    {
        if (!Ports.TryGetValue(sender, out var from) || !Ports.TryGetValue(receiver, out var to) || !from.Output || to.Output) return false;
        var graph = Capture();
        return StructurallySafe(graph with { Edges = [.. graph.Edges, new(from.NodeId, FlowLimits.OutputPort, to.NodeId, FlowLimits.InputPort)] });
    }

    // The shared safe draft validator permits incomplete editing without weakening typed gestures.
    public static bool StructurallySafe(FlowGraphDto graph) => FlowGraphValidator.ValidateDraft(graph).Valid;

    public static string InputType(FlowNodeKind kind) => kind == FlowNodeKind.CreateIncident ? "incident" : "alert";
    public static string OutputType(FlowNodeKind kind) => kind == FlowNodeKind.MapIncident ? "incident" : "alert";

    // ExecuteAsync queues work. Subscribe before execution and await the matching native completion to prevent replay races.
    public static async Task CompleteAsync(IVeloxCommand command, object? parameter, CancellationToken token = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Matches(CommandEventArgs args) => ReferenceEquals(args.Parameter, parameter) || Equals(args.Parameter, parameter);
        void Complete(CommandEventArgs args) { if (Matches(args)) completion.TrySetResult(); }
        void Fail(CommandEventArgs args) { if (Matches(args)) completion.TrySetException(args.Exception ?? new InvalidOperationException("Canvas operation failed.")); }
        void Cancel(CommandEventArgs args) { if (Matches(args)) completion.TrySetCanceled(); }
        command.Completed += Complete; command.Failed += Fail; command.Canceled += Cancel;
        try { token.ThrowIfCancellationRequested(); await command.ExecuteAsync(parameter); await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
        finally { command.Completed -= Complete; command.Failed -= Fail; command.Canceled -= Cancel; }
    }

    public async ValueTask DisposeAsync() => await Tree.GetHelper().CloseAsync();
    private sealed class TypedTreeHelper(FlowCanvasAdapter owner) : TreeHelper
    {
        public override bool ValidateConnection(IWorkflowSlotViewModel sender, IWorkflowSlotViewModel receiver) => owner.CanConnect(sender, receiver);
        public override void ReceiveConnection(IWorkflowSlotViewModel slot) { base.ReceiveConnection(slot); owner.Changed?.Invoke(); }
    }
    private sealed class InertNodeHelper(FlowCanvasAdapter owner) : NodeHelper
    {
        public override void Move(Offset offset) { base.Move(offset); owner.Changed?.Invoke(); }
        public override Task<object?> ReceiveAsync(ITaskContext context, CancellationToken token)
        { owner.ExecutionCalls++; throw new InvalidOperationException("An editor node cannot execute."); }
    }
}

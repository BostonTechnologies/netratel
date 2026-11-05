using NetRatel.Shared.Contracts.Flows;
using VeloxDev.MVVM;
using VeloxDev.WorkflowSystem;

namespace NetRatel.Web.Services.Flows;

/// <summary>Canonical DTOs are translated to reviewed, inert Velox models without migrating published history.</summary>
public sealed class FlowCanvasAdapter : IAsyncDisposable
{
    public event Action? Changed;
    public TreeDefaultViewModel Tree { get; } = new();
    public Dictionary<IWorkflowNodeViewModel, FlowNodeDto> Metadata { get; } = new();
    // Preserve the existing v1 adapter API; named identities are carried separately.
    public Dictionary<IWorkflowSlotViewModel, (Guid NodeId, bool Output)> Ports { get; } = new();
    public Dictionary<IWorkflowSlotViewModel, string> PortIds { get; } = new();
    private readonly Dictionary<IWorkflowLinkViewModel, FlowEdgeDto> _edges = new();
    private Dictionary<(Guid NodeId, string PortId), IReadOnlyList<Guid>>? _compatibleInputs;
    public long StructureRevision { get; private set; }
    private void InvalidateCompatibility() { _compatibleInputs = null; StructureRevision++; }
    public int SchemaVersion { get; private set; } = FlowLimits.SchemaVersion;
    public int MaximumNodes => SchemaVersion == FlowGraphV2Limits.SchemaVersion ? FlowGraphV2Limits.MaximumNodes : FlowLimits.MaximumNodes;
    public int ExecutionCalls { get; private set; }

    public FlowCanvasAdapter()
    {
        Tree.SetHelper(new TypedTreeHelper(this));
        Tree.GetHelper().LinkAdded += RecordLink;
        Tree.GetHelper().LinkRemoved += ForgetLink;
    }
    public IReadOnlyList<FlowPortDescriptorDto> NodePorts(FlowNodeKind kind)
    {
        if (SchemaVersion == FlowGraphV2Limits.SchemaVersion)
            return FlowNodeCatalogue.Find(kind)?.Ports ?? [];
        var ports = new List<FlowPortDescriptorDto>();
        if (kind != FlowNodeKind.AlertRaised) ports.Add(new(FlowLimits.InputPort, "In", FlowPortDirection.Input, "schema-1", InputType(kind)));
        if (!FlowGraphValidator.IsTerminal(kind)) ports.Add(new(FlowLimits.OutputPort, "Out", FlowPortDirection.Output, "schema-1", OutputType(kind)));
        return ports;
    }
    public Size DesignSize(FlowNodeKind kind) => SchemaVersion == FlowGraphV2Limits.SchemaVersion
        ? new(280, Math.Max(164, 112 + 28 * Math.Max(NodePorts(kind).Count(port => port.Direction == FlowPortDirection.Input),
            NodePorts(kind).Count(port => port.Direction == FlowPortDirection.Output)))) : new(220, 140);

    public async Task<IWorkflowNodeViewModel> AddAsync(FlowNodeDto definition, CancellationToken cancellationToken = default)
    {
        if (Tree.Nodes.Count >= MaximumNodes || Metadata.Values.Any(node => node.Id == definition.Id))
            throw new InvalidOperationException("Node limit or duplicate node ID.");
        if (!Enum.IsDefined(definition.Kind) || SchemaVersion == 1 && (int)definition.Kind > 6 ||
            SchemaVersion == 2 && FlowNodeCatalogue.Find(definition.Kind) is null)
            throw new InvalidOperationException("This node kind is not supported by the graph schema.");
        var node = new NodeDefaultViewModel();
        node.SetHelper(new InertNodeHelper(this));
        Metadata.Add(node, definition); InvalidateCompatibility();
        await CompleteAsync(Tree.CreateNodeCommand, node, cancellationToken);
        await CompleteAsync(node.SetAnchorCommand, new Anchor(definition.Position.X, definition.Position.Y), cancellationToken);
        await CompleteAsync(node.SetSizeCommand, DesignSize(definition.Kind), cancellationToken);
        foreach (var port in NodePorts(definition.Kind))
            await AddPortAsync(node, definition.Id, port, cancellationToken);
        return node;
    }
    private async Task AddPortAsync(IWorkflowNodeViewModel node, Guid id, FlowPortDescriptorDto descriptor, CancellationToken token)
    {
        var slot = new SlotDefaultViewModel();
        var output = descriptor.Direction == FlowPortDirection.Output;
        Ports.Add(slot, (id, output)); PortIds.Add(slot, descriptor.Id);
        await CompleteAsync(node.CreateSlotCommand, slot, token);
        await CompleteAsync(slot.SetChannelCommand, output ? SlotChannel.OneTarget :
            SchemaVersion == 2 ? SlotChannel.MultipleSources : SlotChannel.OneSource, token);
    }
    private string DefaultOutput(Guid node)
    {
        if (SchemaVersion == 1) return FlowLimits.OutputPort;
        var outputs = Ports.Where(port => port.Value.NodeId == node && port.Value.Output).Select(port => PortIds[port.Key]).ToArray();
        return outputs.Length == 1 ? outputs[0] : throw new InvalidOperationException("Choose the named outcome port.");
    }
    public Task ConnectAsync(Guid source, Guid target, CancellationToken token = default) =>
        ConnectAsync(source, DefaultOutput(source), target, FlowLimits.InputPort, token);
    public async Task ConnectAsync(Guid source, string sourcePort, Guid target, string targetPort, CancellationToken token = default)
    {
        var from = FindPort(source, sourcePort, true);
        var to = FindPort(target, targetPort, false);
        if (from is null || to is null || !CanConnect(from, to))
            throw new InvalidOperationException("Choose compatible unused outcome ports on an acyclic path.");
        await CompleteAsync(Tree.SendConnectionCommand, from, token);
        try { await CompleteAsync(Tree.ReceiveConnectionCommand, to, token); }
        finally { await CompleteAsync(Tree.ResetVirtualLinkCommand, null, token); }
        RecordLink(this, Tree.Links.Single(link => ReferenceEquals(link.Sender, from) && ReferenceEquals(link.Receiver, to)));
        Changed?.Invoke();
    }
    private IWorkflowSlotViewModel? FindPort(Guid node, string id, bool output) =>
        Ports.SingleOrDefault(port => port.Value == (node, output) && PortIds[port.Key] == id).Key;
    private void RecordLink(object? sender, IWorkflowLinkViewModel link)
    {
        if (_edges.ContainsKey(link) || !Ports.TryGetValue(link.Sender, out var source) || !Ports.TryGetValue(link.Receiver, out var target)) return;
        _edges.Add(link, new(source.NodeId, PortIds[link.Sender], target.NodeId, PortIds[link.Receiver])
        { Id = SchemaVersion == 2 ? Guid.NewGuid() : null });
        InvalidateCompatibility();
    }
    private void ForgetLink(object? sender, IWorkflowLinkViewModel link) { _edges.Remove(link); InvalidateCompatibility(); }
    public FlowEdgeDto Edge(IWorkflowLinkViewModel link) => _edges.TryGetValue(link, out var edge) ? edge :
        throw new InvalidOperationException("This connection has no canonical identity.");
    public async Task RemoveAsync(Guid id, CancellationToken token = default)
    {
        var node = Metadata.Single(pair => pair.Value.Id == id).Key;
        var slots = Ports.Where(pair => pair.Value.NodeId == id).Select(pair => pair.Key).ToHashSet();
        foreach (var link in Tree.Links.ToArray().Where(link => slots.Contains(link.Sender) || slots.Contains(link.Receiver)))
            await CompleteAsync(link.DeleteCommand, null, token);
        await CompleteAsync(node.DeleteCommand, null, token);
        foreach (var slot in slots) { Ports.Remove(slot); PortIds.Remove(slot); }
        Metadata.Remove(node); InvalidateCompatibility(); Changed?.Invoke();
    }
    public async Task DisconnectAsync(FlowEdgeDto edge, CancellationToken token = default)
    {
        var link = _edges.Single(pair => pair.Value.SourceNodeId == edge.SourceNodeId && pair.Value.SourcePort == edge.SourcePort &&
            pair.Value.TargetNodeId == edge.TargetNodeId && pair.Value.TargetPort == edge.TargetPort).Key;
        await CompleteAsync(link.DeleteCommand, null, token);
        Changed?.Invoke();
    }
    public void Update(FlowNodeDto definition)
    {
        var node = Metadata.Single(pair => pair.Value.Id == definition.Id).Key;
        if (Metadata[node].Kind != definition.Kind) InvalidateCompatibility();
        Metadata[node] = definition; Changed?.Invoke();
    }
    public void SetRoute(Guid edgeId, IReadOnlyList<FlowPositionDto>? points)
    {
        if (SchemaVersion != FlowGraphV2Limits.SchemaVersion) throw new InvalidOperationException("Saved routes require an explicitly upgraded schema-two draft.");
        var pair = _edges.SingleOrDefault(pair => pair.Value.Id == edgeId);
        if (pair.Key is null) throw new InvalidOperationException("The selected connection is unavailable.");
        var next = pair.Value with { Route = points is { Count: > 0 } ? new(points.ToArray()) : null };
        var graph = Capture();
        var candidate = graph with { Edges = graph.Edges.Select(edge => edge.Id == edgeId ? next : edge).ToArray() };
        if (!FlowGraphV2Validator.ValidateDraft(candidate).Valid) throw new InvalidOperationException($"Use at most {FlowGraphV2Limits.MaximumRoutePoints} finite route points within the canvas bounds.");
        _edges[pair.Key] = next; Changed?.Invoke();
    }
    public FlowGraphDto Capture(double scrollX = 0, double scrollY = 0)
    {
        var layout = Tree.Layout; var scale = layout.Scale.Horizontal;
        var nodes = Tree.Nodes.ToArray().Select(node => Metadata.TryGetValue(node, out var definition) ? definition with
        { Position = new(Math.Round(node.Anchor.Horizontal * scale, 6), Math.Round(node.Anchor.Vertical * scale, 6)) } : null).OfType<FlowNodeDto>().ToArray();
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();
        var links = Tree.Links.ToArray();
        if (links.Any(link => !_edges.ContainsKey(link))) throw new InvalidOperationException("A connection has no canonical identity; reload before saving.");
        var edges = links.Select(Edge).Where(edge => nodeIds.Contains(edge.SourceNodeId) && nodeIds.Contains(edge.TargetNodeId)).ToArray();
        return new(SchemaVersion, nodes, edges, new(scale, scrollX, scrollY,
            layout.NegativeOffset.Horizontal, layout.NegativeOffset.Vertical, layout.PositiveOffset.Horizontal, layout.PositiveOffset.Vertical));
    }
    public async Task RestoreAsync(FlowGraphDto graph, CancellationToken token = default)
    {
        if (!StructurallySafe(graph) || Tree.Nodes.Count != 0) throw new InvalidOperationException("Unsupported or unsafe draft geometry/connections.");
        SchemaVersion = graph.SchemaVersion;
        foreach (var node in graph.Nodes) await AddAsync(node, token);
        Tree.Layout.NegativeOffset = new Offset(graph.Viewport.NegativeX, graph.Viewport.NegativeY);
        Tree.Layout.PositiveOffset = new Offset(graph.Viewport.PositiveX, graph.Viewport.PositiveY);
        foreach (var edge in graph.Edges)
        {
            await ConnectAsync(edge.SourceNodeId, edge.SourcePort, edge.TargetNodeId, edge.TargetPort, token);
            var from = FindPort(edge.SourceNodeId, edge.SourcePort, true); var to = FindPort(edge.TargetNodeId, edge.TargetPort, false);
            var link = Tree.Links.Single(link => ReferenceEquals(link.Sender, from) && ReferenceEquals(link.Receiver, to));
            _edges[link] = edge;
        }
        Tree.Layout.Scale = new Scale(graph.Viewport.Scale, graph.Viewport.Scale);
    }
    /// <summary>Structural highlights are computed once per topology revision; final drops still run the full validator.</summary>
    public IReadOnlyList<Guid> CompatibleInputs(Guid source, string sourcePort)
    {
        _compatibleInputs ??= BuildCompatibility();
        return _compatibleInputs.GetValueOrDefault((source, sourcePort)) ?? [];
    }
    private Dictionary<(Guid, string), IReadOnlyList<Guid>> BuildCompatibility()
    {
        var nodes = Metadata.Values.ToDictionary(node => node.Id);
        var live = Tree.Links.ToHashSet();
        var edges = _edges.Where(pair => live.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
        var outgoing = nodes.Keys.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var edge in edges)
            if (outgoing.TryGetValue(edge.SourceNodeId, out var targets) && nodes.ContainsKey(edge.TargetNodeId)) targets.Add(edge.TargetNodeId);
        var descendants = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var id in nodes.Keys)
        {
            var reached = new HashSet<Guid>(); var pending = new Stack<Guid>(outgoing[id]);
            while (pending.TryPop(out var next))
                if (reached.Add(next)) foreach (var target in outgoing[next]) pending.Push(target);
            descendants[id] = reached;
        }
        var occupiedOutputs = edges.Select(edge => (edge.SourceNodeId, edge.SourcePort)).ToHashSet();
        var occupiedInputs = edges.Select(edge => edge.TargetNodeId).ToHashSet();
        var result = new Dictionary<(Guid, string), IReadOnlyList<Guid>>();
        foreach (var node in nodes.Values)
        foreach (var port in NodePorts(node.Kind).Where(port => port.Direction == FlowPortDirection.Output))
        {
            result[(node.Id, port.Id)] = occupiedOutputs.Contains((node.Id, port.Id)) ? [] : nodes.Values
                .Where(target => target.Id != node.Id && !descendants[target.Id].Contains(node.Id) &&
                    NodePorts(target.Kind).Any(input => input.Direction == FlowPortDirection.Input) &&
                    (SchemaVersion == 2
                        ? FlowNodeCatalogue.CanConnect(node.Kind, port.Id, target.Kind, FlowNodeCatalogue.Input)
                        : !occupiedInputs.Contains(target.Id) && FlowGraphValidator.CanConnect(node.Kind, target.Kind)))
                .Select(target => target.Id).ToArray();
        }
        return result;
    }
    public string? ConnectionRejection(Guid source, Guid target) => ConnectionRejection(source, DefaultOutput(source), target, FlowLimits.InputPort);
    public string? ConnectionRejection(Guid source, string sourcePort, Guid target, string targetPort)
    {
        if (FindPort(source, sourcePort, true) is null || FindPort(target, targetPort, false) is null)
            return "Choose an output and an input port.";
        var graph = Capture();
        var edge = new FlowEdgeDto(source, sourcePort, target, targetPort) { Id = SchemaVersion == 2 ? Guid.NewGuid() : null };
        var candidate = graph with { Edges = [.. graph.Edges, edge] };
        var validation = DraftValidation(candidate);
        if (validation.Valid) return null;
        return validation.Issues.FirstOrDefault()?.Code switch
        {
            "fanout" or "outcome-fanout" or "duplicate-edge" => "This outcome port is already connected. Remove its connection first.",
            "cycle" => "This connection would create a cycle.",
            "typed-port" => "These ports carry incompatible data.",
            _ => "The connection is not permitted by the graph's port and draft validation rules."
        };
    }
    private bool CanConnect(IWorkflowSlotViewModel sender, IWorkflowSlotViewModel receiver) =>
        Ports.TryGetValue(sender, out var source) && Ports.TryGetValue(receiver, out var target) && source.Output && !target.Output &&
        ConnectionRejection(source.NodeId, PortIds[sender], target.NodeId, PortIds[receiver]) is null;
    private static FlowValidationResultDto DraftValidation(FlowGraphDto graph) => graph.SchemaVersion == FlowGraphV2Limits.SchemaVersion
        ? FlowGraphV2Validator.ValidateDraft(graph) : FlowGraphValidator.ValidateDraft(graph);
    public static bool StructurallySafe(FlowGraphDto graph) => DraftValidation(graph).Valid;
    public static string InputType(FlowNodeKind kind) => FlowGraphValidator.IsTerminal(kind) ? "incident" : "alert context";
    public static string OutputType(FlowNodeKind kind) => kind switch
    { FlowNodeKind.MapIncident => "mapped alert", FlowNodeKind.InvestigateWithAi => "alert + AI result", _ => "alert" };

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


    public async ValueTask DisposeAsync()
    {
        Tree.GetHelper().LinkAdded -= RecordLink; Tree.GetHelper().LinkRemoved -= ForgetLink;
        await Tree.GetHelper().CloseAsync();
    }
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

public readonly record struct FlowRoutePointEdit(Guid EdgeId, int Index, FlowPositionDto Position);

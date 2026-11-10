namespace NetRatel.Shared.Contracts.Flows;

/// <summary>Draft-only identity remapping. Immutable published versions and their protected pins are never cloned.</summary>
public static class FlowGraphDraftClone
{
    public static FlowGraphDto SchemaTwo(FlowGraphDto graph)
    {
        if (graph.SchemaVersion != FlowGraphV2Limits.SchemaVersion) throw new ArgumentException("A schema-two draft is required.", nameof(graph));
        var ids = graph.Nodes.ToDictionary(node => node.Id, _ => Guid.NewGuid());
        FlowInputBindingDto Binding(FlowInputBindingDto binding) => binding.NodeId is { } id && ids.TryGetValue(id, out var mapped)
            ? binding with { NodeId = mapped } : binding;
        return graph with
        {
            Nodes = graph.Nodes.Select(node => node with
            {
                Id = ids[node.Id],
                Actions = node.Actions?.Select(action => action with { Id = Guid.NewGuid() }).ToArray(),
                Inputs = node.Inputs?.ToDictionary(pair => pair.Key, pair => Binding(pair.Value), StringComparer.Ordinal),
                Predicate = node.Predicate is { } predicate ? predicate with { Input = Binding(predicate.Input) } : null,
                ExecutionAction = node.ExecutionAction is { } action ? action with
                {
                    PinnedSnapshotReference = null,
                    TargetBinding = action.TargetBinding is { } target ? target with { Value = Binding(target.Value) } : null
                } : null,
                AiSettings = node.AiSettings is { } settings ? settings with { PinnedPrompts = null } : null
            }).ToArray(),
            Edges = graph.Edges.Select(edge => edge with
            { Id = Guid.NewGuid(), SourceNodeId = ids.GetValueOrDefault(edge.SourceNodeId, edge.SourceNodeId),
                TargetNodeId = ids.GetValueOrDefault(edge.TargetNodeId, edge.TargetNodeId) }).ToArray()
        };
    }
}

using System.Text;
using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Web.Services.Flows;

/// <summary>Presentation-only authoring helpers. The server validates every binding at publish and execution.</summary>
public static class FlowAuthoringContext
{
    public static bool TryParseFiniteNumber(string text, out JsonElement value)
    {
        value = default;
        if (Encoding.UTF8.GetByteCount(text) > FlowGraphV2Limits.MaximumBindingBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            var number = document.RootElement;
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out var finite) || !double.IsFinite(finite)) return false;
            value = number.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static IReadOnlyList<FlowNodeDto> AvailableProducers(FlowGraphDto graph, Guid nodeId)
    {
        var ids = graph.Nodes.Select(node => node.Id).ToHashSet();
        if (!ids.Contains(nodeId)) return [];
        var incoming = ids.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var edge in graph.Edges)
            if (ids.Contains(edge.SourceNodeId) && incoming.TryGetValue(edge.TargetNodeId, out var predecessors)) predecessors.Add(edge.SourceNodeId);
        var triggers = graph.Nodes.Where(node => FlowNodeCatalogue.IsTrigger(node.Kind)).Take(2).ToArray();
        if (triggers.Length != 1) return [];
        var trigger = triggers[0];
        var dominators = ids.ToDictionary(id => id, id => id == trigger.Id ? new HashSet<Guid> { id } : new HashSet<Guid>(ids));
        for (var pass = 0; pass < ids.Count; pass++)
        {
            var changed = false;
            foreach (var id in ids.Where(id => id != trigger.Id))
            {
                var predecessors = incoming[id];
                var next = predecessors.Count == 0 ? new HashSet<Guid>() : new HashSet<Guid>(dominators[predecessors[0]]);
                foreach (var previous in predecessors.Skip(1)) next.IntersectWith(dominators[previous]);
                next.Add(id);
                if (!next.SetEquals(dominators[id])) { dominators[id] = next; changed = true; }
            }
            if (!changed) break;
        }
        return graph.Nodes.Where(node => node.Id != nodeId && dominators[nodeId].Contains(node.Id) &&
            FlowBindingCatalogue.OutputFields(node.Kind).Count > 0).ToArray();
    }
    public static IReadOnlyList<FlowBindingFieldDto> EventFields(FlowGraphDto graph) =>
        graph.Nodes.Any(node => node.Kind == FlowNodeKind.ExecutionEvent) ? FlowBindingCatalogue.ExecutionFields : FlowBindingCatalogue.AlertFields;
}

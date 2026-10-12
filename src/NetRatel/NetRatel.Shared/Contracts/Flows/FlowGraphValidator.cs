using System.Text.Json;

namespace NetRatel.Shared.Contracts.Flows;

/// <summary>Canonical data validation only. This type never constructs or executes a workflow/runtime.</summary>
public static class FlowGraphValidator
{
    private static readonly HashSet<string> TemplateFields = new(StringComparer.Ordinal)
        { "ruleName", "clientName", "resource", "metric", "severity", "value", "serviceState", "occurredAt", "offlineSummary", "offlineCount", "offlineClients" };
    public static FlowValidationResultDto ValidateDraft(FlowGraphDto? graph) => Validate(graph, false, false);
    public static FlowValidationResultDto ValidatePublished(FlowGraphDto? graph) => Validate(graph, true, true);
    public static FlowValidationResultDto ValidateComplete(FlowGraphDto? graph) => Validate(graph, true, false);

    private static FlowValidationResultDto Validate(FlowGraphDto? graph, bool complete, bool pinned)
    {
        var errors = new List<FlowValidationIssueDto>();
        void Error(string code, Guid? id = null) => errors.Add(new(code, id));
        if (graph is null || graph.SchemaVersion != FlowLimits.SchemaVersion || graph.Nodes is null || graph.Edges is null || graph.Viewport is null)
            return new(false, [new("graph-schema")]);
        if (graph.Nodes.Count > FlowLimits.MaximumNodes || graph.Edges.Count > FlowLimits.MaximumEdges)
            return new(false, [new("graph-limit")]);
        try { if (JsonSerializer.SerializeToUtf8Bytes(graph).Length > FlowLimits.MaximumGraphBytes) return new(false, [new("graph-bytes")]); }
        catch (ArgumentException) { return new(false, [new("graph-value")]); }
        if (!ValidViewport(graph.Viewport)) Error("viewport-limit");
        var nodes = new Dictionary<Guid, FlowNodeDto>();
        var actionIds = new HashSet<Guid>();
        foreach (var node in graph.Nodes)
        {
            if (node is null || node.Id == Guid.Empty || !nodes.TryAdd(node.Id, node)) { Error("node-id"); continue; }
            if (!Enum.IsDefined(node.Kind)) Error("node-kind", node.Id);
            if (!IsBoundedText(node.Name, FlowLimits.MaximumNameLength) || node.Position is null || !ValidCoordinate(node.Position.X) || !ValidCoordinate(node.Position.Y)) Error("node-layout", node.Id);
            if (node.Kind != FlowNodeKind.Condition && node.Condition is not null || node.Kind != FlowNodeKind.MapIncident && node.Mapping is not null ||
                node.Kind != FlowNodeKind.CreateIncident && (node.ConnectorId is not null || node.ConnectorRevision is not null) ||
                node.Kind != FlowNodeKind.ActionGroup && node.Actions is not null) Error("node-properties", node.Id);
            if (node.Kind == FlowNodeKind.Condition)
            {
                if (node.Condition is { } condition && !ValidCondition(condition)) Error("condition", node.Id);
                if (complete && node.Condition is null) Error("condition-required", node.Id);
            }
            if (node.Kind == FlowNodeKind.MapIncident)
            {
                if (node.Mapping is { } mapping && (!ValidTemplate(mapping.TitleTemplate, FlowLimits.MaximumTitleLength) ||
                    !ValidTemplate(mapping.DescriptionTemplate, FlowLimits.MaximumTemplateLength, true) || mapping.Priority is < 0 or > 3)) Error("mapping", node.Id);
                if (complete && node.Mapping is null) Error("mapping-required", node.Id);
            }
            if (node.Kind == FlowNodeKind.CreateIncident)
            {
                if (!actionIds.Add(node.Id)) Error("action-id", node.Id);
                if (node.ConnectorId == Guid.Empty || node.ConnectorRevision is <= 0) Error("connector-reference", node.Id);
                if (complete && node.ConnectorId is null) Error("connector-required", node.Id);
                if (pinned && node.ConnectorRevision is null) Error("connector-revision-required", node.Id);
            }
            if (node.Kind == FlowNodeKind.ActionGroup)
            {
                if (node.Actions?.Count > FlowLimits.MaximumActionsPerGroup) Error("action-limit", node.Id);
                if (complete && node.Actions is not { Count: > 0 }) Error("actions-required", node.Id);
                foreach (var action in node.Actions ?? [])
                {
                    if (action is null || action.Id == Guid.Empty || !actionIds.Add(action.Id)) { Error("action-id", node.Id); continue; }
                    if (!Enum.IsDefined(action.Kind)) Error("action-kind", node.Id);
                    if (action.Name is not null && !IsBoundedText(action.Name, FlowLimits.MaximumNameLength)) Error("action-name", node.Id);
                    if (action.ConnectorId == Guid.Empty || action.ConnectorRevision is <= 0) Error("connector-reference", node.Id);
                    if (complete && action.ConnectorId is null) Error("connector-required", node.Id);
                    if (pinned && action.ConnectorRevision is null) Error("connector-revision-required", node.Id);
                }
                if ((node.Actions ?? []).Where(action => action?.ConnectorId is not null)
                    .GroupBy(action => (action.Kind, action.ConnectorId)).Any(group => group.Count() > 1)) Error("duplicate-action-target", node.Id);
            }
        }
        // Child IDs share the durable action namespace; they cannot alias any other graph node.
        foreach (var group in nodes.Values.Where(node => node.Kind == FlowNodeKind.ActionGroup))
            if ((group.Actions ?? []).Any(action => action is not null && nodes.ContainsKey(action.Id))) Error("action-id", group.Id);
        var incoming = new Dictionary<Guid, Guid>();
        var outgoing = new Dictionary<Guid, Guid>();
        foreach (var edge in graph.Edges)
        {
            if (edge is null || !nodes.TryGetValue(edge.SourceNodeId, out var source) || !nodes.TryGetValue(edge.TargetNodeId, out var target)) { Error("edge-node"); continue; }
            if (edge.SourcePort != FlowLimits.OutputPort || edge.TargetPort != FlowLimits.InputPort || !CanConnect(source.Kind, target.Kind)) Error("typed-port");
            if (!outgoing.TryAdd(source.Id, target.Id) || !incoming.TryAdd(target.Id, source.Id)) Error("fanout");
        }
        foreach (var id in nodes.Keys)
        {
            var seen = new HashSet<Guid>();
            var current = id;
            while (outgoing.TryGetValue(current, out var next))
            {
                if (!seen.Add(current)) { Error("cycle", id); break; }
                current = next;
            }
        }
        if (complete)
        {
            if (nodes.Values.Count(node => node.Kind == FlowNodeKind.AlertRaised) != 1 || nodes.Values.Count(node => node.Kind == FlowNodeKind.MapIncident) != 1 ||
                nodes.Values.Count(node => IsTerminal(node.Kind)) != 1 || nodes.Values.Count(node => node.Kind == FlowNodeKind.Condition) > 1) Error("node-catalogue");
            var trigger = nodes.Values.SingleOrDefaultSafe(node => node.Kind == FlowNodeKind.AlertRaised);
            if (trigger is not null)
            {
                var visited = new HashSet<Guid>(); var current = trigger.Id;
                while (visited.Add(current) && outgoing.TryGetValue(current, out var next)) current = next;
                if (visited.Count != nodes.Count || !nodes.TryGetValue(current, out var terminal) || !IsTerminal(terminal.Kind)) Error("unreachable-path");
            }
            foreach (var node in nodes.Values)
            {
                if (node.Kind == FlowNodeKind.AlertRaised ? incoming.ContainsKey(node.Id) : !incoming.ContainsKey(node.Id)) Error("input-path", node.Id);
                if (IsTerminal(node.Kind) ? outgoing.ContainsKey(node.Id) : !outgoing.ContainsKey(node.Id)) Error("output-path", node.Id);
            }
        }
        return new(errors.Count == 0, errors.Distinct().ToArray());
    }

    public static bool CanConnect(FlowNodeKind source, FlowNodeKind target) => source switch
    {
        FlowNodeKind.AlertRaised or FlowNodeKind.Condition => target is FlowNodeKind.Condition or FlowNodeKind.MapIncident,
        FlowNodeKind.MapIncident => IsTerminal(target),
        _ => false
    };
    public static bool IsTerminal(FlowNodeKind kind) => kind is FlowNodeKind.CreateIncident or FlowNodeKind.ActionGroup;

    public static IReadOnlyList<FlowActionTargetDto> ActionTargets(FlowGraphDto graph) => graph.Nodes.SelectMany(node =>
        node.Kind == FlowNodeKind.CreateIncident
            ? (IEnumerable<FlowActionTargetDto>)[new(node.Id, FlowActionKind.RatelDeskIncident, node.ConnectorId, node.ConnectorRevision)]
            : node.Kind == FlowNodeKind.ActionGroup ? node.Actions ?? [] : []).ToArray();
    public static bool ValidCondition(FlowConditionDto condition) => Enum.IsDefined(condition.Field) && Enum.IsDefined(condition.Comparison) &&
        (condition.Field == FlowValueField.NumericValue
            ? condition.NumericValue is { } value && double.IsFinite(value) && condition.TextValue is null
            : condition.NumericValue is null && IsBoundedText(condition.TextValue, FlowLimits.MaximumEventTextLength) && condition.Comparison is FlowComparison.Equal or FlowComparison.NotEqual);

    public static bool ValidTemplate(string? template, int maximum, bool multiline = false)
    {
        if (!IsBoundedText(template, maximum, multiline: multiline)) return false;
        for (var index = 0; index < template!.Length; index++)
        {
            if (template[index] == '}') return false;
            if (template[index] != '{') continue;
            var end = template.IndexOf('}', index + 1);
            if (end < 0 || !TemplateFields.Contains(template[(index + 1)..end])) return false;
            index = end;
        }
        return true;
    }

    public static bool IsBoundedText(string? value, int maximum, bool allowEmpty = false, bool multiline = false)
    {
        if (value is null || value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value))) return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character)) { if (++index == value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(character) || char.IsControl(character) && !(multiline && character is '\r' or '\n' or '\t')) return false;
        }
        return true;
    }
    private static bool ValidCoordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= 20000;
    private static bool ValidViewport(FlowViewportDto viewport) => double.IsFinite(viewport.Scale) && viewport.Scale is >= .1 and <= 10 &&
        new[] { viewport.ScrollX, viewport.ScrollY, viewport.NegativeX, viewport.NegativeY, viewport.PositiveX, viewport.PositiveY }.All(ValidCoordinate);
    private static T? SingleOrDefaultSafe<T>(this IEnumerable<T> source, Func<T, bool> predicate) where T : class
    {
        var matches = source.Where(predicate).Take(2).ToArray(); return matches.Length == 1 ? matches[0] : null;
    }
}

public static class FlowGraphTemplates
{
    public static FlowGraphDto IncidentFromAlert()
    {
        var trigger = Guid.NewGuid(); var mapping = Guid.NewGuid(); var action = Guid.NewGuid();
        return new(FlowLimits.SchemaVersion,
            [new(trigger, FlowNodeKind.AlertRaised, "Alert raised", new(60, 160)),
             new(mapping, FlowNodeKind.MapIncident, "Map alert fields", new(350, 160), Mapping: new("{severity}: {ruleName} on {clientName}", "{ruleName}\nClient: {clientName}\nResource: {resource}\nMetric: {metric}\nValue: {value}\nService: {serviceState}\nObserved: {occurredAt}")),
             new(action, FlowNodeKind.CreateIncident, "RatelDesk: Create incident", new(640, 160))],
            [new(trigger, FlowLimits.OutputPort, mapping, FlowLimits.InputPort), new(mapping, FlowLimits.OutputPort, action, FlowLimits.InputPort)], new());
    }
}

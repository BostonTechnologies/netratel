using System.Text;
using System.Text.Json;

namespace NetRatel.Shared.Contracts.Flows;

/// <summary>
/// Pure schema-two validation. The schema-one persistence/runtime gate is intentionally separate until
/// the durable v2 executor is enabled. Exclusive outcome edges never imply parallel execution or a join.
/// </summary>
public static class FlowGraphV2Validator
{
    private static readonly HashSet<string> TemplateFields = new(StringComparer.Ordinal)
    {
        "ruleName", "clientName", "resource", "metric", "severity", "value", "serviceState", "occurredAt",
        "offlineSummary", "offlineCount", "offlineClients", "aiSummary", "aiEvidence", "aiRecommendations",
        "aiStatus", "aiObservedAt", "aiInvestigationId", "sourceKind", "definitionName", "definitionRevision",
        "executionId", "attemptId", "targetName", "outcome", "rawStatus", "reason", "exitCode",
        "startedAt", "completedAt", "executionLink"
    };

    public static FlowValidationResultDto ValidateDraft(FlowGraphDto? graph) => Validate(graph, false, false);
    public static FlowValidationResultDto ValidateComplete(FlowGraphDto? graph) => Validate(graph, true, false);
    public static FlowValidationResultDto ValidatePublished(FlowGraphDto? graph) => Validate(graph, true, true);

    private static FlowValidationResultDto Validate(FlowGraphDto? graph, bool complete, bool pinned)
    {
        if (graph is null || graph.SchemaVersion != FlowGraphV2Limits.SchemaVersion ||
            graph.Nodes is null || graph.Edges is null || graph.Viewport is null)
            return new(false, [new("graph-schema")]);
        if (graph.Nodes.Count > FlowGraphV2Limits.MaximumNodes || graph.Edges.Count > FlowGraphV2Limits.MaximumEdges)
            return new(false, [new("graph-limit")]);
        try
        {
            if (JsonSerializer.SerializeToUtf8Bytes(graph).Length > FlowGraphV2Limits.MaximumGraphBytes)
                return new(false, [new("graph-bytes")]);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { return new(false, [new("graph-value")]); }

        var issues = new List<FlowValidationIssueDto>();
        void Error(string code, Guid? nodeId = null, Guid? edgeId = null) =>
            issues.Add(new(code, nodeId) { EdgeId = edgeId });
        if (!ValidViewport(graph.Viewport)) Error("viewport-limit");
        var nodes = new Dictionary<Guid, FlowNodeDto>();
        var childActionIds = new HashSet<Guid>();
        foreach (var node in graph.Nodes)
        {
            if (node is null || node.Id == Guid.Empty || !nodes.TryAdd(node.Id, node))
            { Error("node-id"); continue; }
            if (FlowNodeCatalogue.Find(node.Kind) is null) { Error("node-kind", node.Id); continue; }
            if (!Text(node.Name, FlowLimits.MaximumNameLength) || node.Position is null ||
                !Coordinate(node.Position.X) || !Coordinate(node.Position.Y)) Error("node-layout", node.Id);

            // Retain existing connector, prompt and independent-action validation without teaching v1 new kinds.
            if ((int)node.Kind <= 6)
            {
                var legacy = node with { Mapping = null, ExecutionTrigger = null, ExecutionAction = null,
                    Rerun = null, Predicate = null, Inputs = null };
                var legacyIssues = FlowGraphValidator.ValidateDraft(new(1, [legacy], [], new())).Issues;
                foreach (var issue in legacyIssues) issues.Add(issue);
            }
            else if (node.Condition is not null || node.Mapping is not null || node.Actions is not null ||
                node.AiSettings is not null || node.ConnectorId is not null || node.ConnectorRevision is not null)
                Error("node-properties", node.Id);
            if (node.Kind != FlowNodeKind.ExecutionEvent && node.ExecutionTrigger is not null ||
                node.Kind is not (FlowNodeKind.RunLibraryScript or FlowNodeKind.RunJob) && node.ExecutionAction is not null ||
                node.Kind != FlowNodeKind.RerunOriginalExecution && node.Rerun is not null ||
                node.Kind != FlowNodeKind.Condition && node.Predicate is not null ||
                node.Kind is not (FlowNodeKind.RunLibraryScript or FlowNodeKind.RunJob or FlowNodeKind.MapIncident) && node.Inputs is not null)
                Error("node-properties", node.Id);

            if (node.Kind == FlowNodeKind.ExecutionEvent &&
                (node.ExecutionTrigger is { } trigger ? !ValidTrigger(trigger) : complete))
                Error("execution-trigger", node.Id);
            if (node.Kind == FlowNodeKind.Condition)
            {
                if (node.Predicate is not null && node.Condition is not null) Error("condition-ambiguous", node.Id);
                if (complete && node.Predicate is null && node.Condition is null) Error("condition-required", node.Id);
                if (node.Predicate is { } predicate &&
                    (!Enum.IsDefined(predicate.Comparison) || !ValidPredicateValue(predicate) ||
                     !ValidBinding(predicate.Input))) Error("condition", node.Id);
            }
            if (node.Kind == FlowNodeKind.MapIncident)
            {
                if (node.Mapping is { } mapping &&
                    (!ValidTemplate(mapping.TitleTemplate, FlowLimits.MaximumTitleLength, node.Inputs) ||
                     !ValidTemplate(mapping.DescriptionTemplate, FlowLimits.MaximumTemplateLength, node.Inputs, true) ||
                     mapping.Priority is < 0 or > 3)) Error("mapping", node.Id);
                if (complete && node.Mapping is null) Error("mapping-required", node.Id);
            }
            if (node.Kind is FlowNodeKind.RunLibraryScript or FlowNodeKind.RunJob)
            {
                var expectedSource = node.Kind == FlowNodeKind.RunJob ? FlowExecutionSourceKind.Job : FlowExecutionSourceKind.Script;
                if (node.ExecutionAction is { } action)
                {
                    if (action.SourceKind != expectedSource || !ValidDefinition(action.Definition) ||
                        action.TimeoutMinutes is < 1 or > FlowGraphV2Limits.MaximumOperationMinutes || !Enum.IsDefined(action.Environment) ||
                        action.WorkingDirectory is not null && !Text(action.WorkingDirectory, 1024) ||
                        action.Target is { AgentId: null } || action.Target?.AgentId == Guid.Empty)
                        Error("execution-action", node.Id);
                    if (node.Kind == FlowNodeKind.RunJob && complete && action.Target?.AgentId is null)
                        Error("job-bound-target-required", node.Id);
                    if (pinned && (action.Definition is null || action.Definition.Revision is null || !Hash(action.Definition.ContentHash) ||
                        !Text(action.PinnedSnapshotReference, 512) || action.Parameters is null))
                        Error("execution-definition-not-pinned", node.Id);
                    var parameters = action.Parameters ?? [];
                    if (parameters.Count > FlowGraphV2Limits.MaximumBindings ||
                        parameters.Any(parameter => parameter is null || !Identifier(parameter.Name) || !Enum.IsDefined(parameter.DataType)) ||
                        parameters.Select(parameter => parameter?.Name).Distinct(StringComparer.Ordinal).Count() != parameters.Count)
                        Error("execution-parameters", node.Id);
                    if (action.Parameters is not null)
                    {
                        foreach (var parameter in parameters.Where(parameter => parameter is not null))
                        {
                            var binding = node.Inputs?.GetValueOrDefault(parameter.Name);
                            if (complete && parameter.Required && binding is null) Error("input-required", node.Id);
                            if (binding is not null && binding.DataType != parameter.DataType) Error("input-type", node.Id);
                        }
                        if (node.Inputs?.Keys.Any(key => !parameters.Any(parameter => parameter?.Name == key)) == true)
                            Error("input-unknown", node.Id);
                    }
                }
                else if (complete) Error("execution-action-required", node.Id);
            }
            if (node.Rerun is { } rerun &&
                (rerun.MaximumAttempts is < 1 or > FlowGraphV2Limits.MaximumRecoveryAttempts ||
                 rerun.TimeoutMinutes is < 1 or > FlowGraphV2Limits.MaximumOperationMinutes))
                Error("rerun-budget", node.Id);
            if (node.Kind == FlowNodeKind.RerunOriginalExecution && complete && node.Rerun is null)
                Error("rerun-settings-required", node.Id);

            if (node.Inputs is { } inputs &&
                (inputs.Count > FlowGraphV2Limits.MaximumBindings || inputs.Any(pair => !Identifier(pair.Key) || !ValidBinding(pair.Value))))
                Error("input-binding", node.Id);
            if (node.Kind is FlowNodeKind.CreateIncident or FlowNodeKind.InvestigateWithAi)
            {
                if (complete && node.ConnectorId is null) Error("connector-required", node.Id);
                if (pinned && node.ConnectorRevision is null) Error("connector-revision-required", node.Id);
                if (pinned && node.AiSettings?.OptionalPrompts is { Count: > 0 } && node.AiSettings.PinnedPrompts is null)
                    Error("ai-prompts-not-pinned", node.Id);
            }
            if (node.Kind == FlowNodeKind.ActionGroup)
            {
                if (complete && node.Actions is not { Count: > 0 }) Error("actions-required", node.Id);
                foreach (var action in node.Actions ?? [])
                {
                    if (action is null) continue;
                    if (!childActionIds.Add(action.Id)) Error("action-id", node.Id);
                    if (complete && action.ConnectorId is null) Error("connector-required", node.Id);
                    if (pinned && action.ConnectorRevision is null) Error("connector-revision-required", node.Id);
                }
            }
        }
        foreach (var group in nodes.Values.Where(node => node.Kind == FlowNodeKind.ActionGroup))
            if ((group.Actions ?? []).Any(action => action is not null && nodes.ContainsKey(action.Id)))
                Error("action-id", group.Id);

        var edgeIds = new HashSet<Guid>();
        var outputs = new HashSet<(Guid, string)>();
        var outgoing = nodes.Keys.ToDictionary(id => id, _ => new List<Guid>());
        var incoming = nodes.Keys.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var edge in graph.Edges)
        {
            if (edge is null) { Error("edge-id"); continue; }
            if (edge.Id is not { } id || id == Guid.Empty || !edgeIds.Add(id)) Error("edge-id", edge.SourceNodeId, edge.Id);
            if (edge.Route is { } route && (route.Points is null || route.Points.Count > FlowGraphV2Limits.MaximumRoutePoints ||
                route.Points.Any(point => point is null || !Coordinate(point.X) || !Coordinate(point.Y))))
                Error("edge-route", edgeId: edge.Id);
            if (!nodes.TryGetValue(edge.SourceNodeId, out var source) || !nodes.TryGetValue(edge.TargetNodeId, out var target))
            { Error("edge-node", edgeId: edge.Id); continue; }
            if (!FlowNodeCatalogue.CanConnect(source.Kind, edge.SourcePort, target.Kind, edge.TargetPort))
                Error("typed-port", target.Id, edge.Id);
            if (!outputs.Add((source.Id, edge.SourcePort))) Error("outcome-fanout", source.Id, edge.Id);
            outgoing[source.Id].Add(target.Id); incoming[target.Id].Add(source.Id);
        }

        var states = new Dictionary<Guid, int>();
        void Visit(Guid id)
        {
            if (states.TryGetValue(id, out var state))
            { if (state == 1) Error("cycle", id); return; }
            states[id] = 1;
            foreach (var next in outgoing[id]) Visit(next);
            states[id] = 2;
        }
        foreach (var id in nodes.Keys) Visit(id);
        var triggers = nodes.Values.Where(node => FlowNodeCatalogue.IsTrigger(node.Kind)).ToArray();
        if (triggers.Length > 1 || complete && triggers.Length != 1) Error("trigger-count");
        if (complete)
        {
            foreach (var node in nodes.Values)
            {
                if (FlowNodeCatalogue.IsTrigger(node.Kind) ? incoming[node.Id].Count != 0 : incoming[node.Id].Count == 0)
                    Error("input-path", node.Id);
                foreach (var port in FlowNodeCatalogue.Find(node.Kind)?.Ports.Where(port => port.Direction == FlowPortDirection.Output) ?? [])
                    if (!outputs.Contains((node.Id, port.Id))) Error("outcome-path-" + port.Id, node.Id);
            }
            if (triggers.Length == 1)
            {
                var reached = new HashSet<Guid>();
                void Reach(Guid id) { if (reached.Add(id)) foreach (var next in outgoing[id]) Reach(next); }
                Reach(triggers[0].Id);
                foreach (var id in nodes.Keys.Where(id => !reached.Contains(id))) Error("unreachable-path", id);
            }
        }

        var dominators = Dominators(nodes.Keys, incoming, triggers.SingleOrDefaultSafe()?.Id);
        bool? executionSource = triggers.SingleOrDefaultSafe() is { } eventTrigger ? eventTrigger.Kind == FlowNodeKind.ExecutionEvent : null;
        foreach (var node in nodes.Values)
        {
            if (executionSource is true && node.Kind == FlowNodeKind.Condition && node.Condition is not null)
                Error("condition-source", node.Id);
            var bindings = (node.Inputs?.Values ?? []).Concat(node.Predicate is { } predicate ? [predicate.Input] : []);
            foreach (var binding in bindings)
            {
                if (!ValidBinding(binding)) { Error("input-binding", node.Id); continue; }
                var actualType = binding.Source switch
                {
                    FlowInputSource.Event => EventType(binding.Path!, executionSource),
                    FlowInputSource.NodeOutput when binding.NodeId is { } producer && nodes.TryGetValue(producer, out var producingNode) =>
                        OutputType(producingNode.Kind, binding.Path!),
                    _ => binding.DataType
                };
                if (actualType is null || actualType != binding.DataType) Error("input-type", node.Id);
                if (binding.Source == FlowInputSource.NodeOutput && (binding.NodeId is not { } sourceId ||
                    !nodes.ContainsKey(sourceId) || sourceId == node.Id ||
                    complete && !dominators[node.Id].Contains(sourceId)))
                    Error("input-unavailable", node.Id);
            }
        }
        return new(issues.Count == 0, issues.Distinct().ToArray());
    }

    public static bool ValidTemplate(string? template, int maximum,
        IReadOnlyDictionary<string, FlowInputBindingDto>? inputs = null, bool multiline = false)
    {
        if (!FlowGraphValidator.IsBoundedText(template, maximum, multiline: multiline)) return false;
        for (var index = 0; index < template!.Length; index++)
        {
            if (template[index] == '}') return false;
            if (template[index] != '{') continue;
            var end = template.IndexOf('}', index + 1);
            if (end < 0) return false;
            var field = template[(index + 1)..end];
            if (!TemplateFields.Contains(field) && !(field.StartsWith("input.", StringComparison.Ordinal) &&
                inputs?.ContainsKey(field[6..]) == true && inputs[field[6..]] is { DataType: not FlowDataType.SecretReference }))
                return false;
            index = end;
        }
        return true;
    }

    private static bool ValidTrigger(FlowExecutionTriggerSettingsDto trigger)
    {
        if (trigger.Sources is not { Count: > 0 and <= 3 } || !trigger.Sources.All(Enum.IsDefined) ||
            trigger.Sources.Distinct().Count() != trigger.Sources.Count ||
            trigger.Outcomes is not { Count: > 0 and <= 7 } || !trigger.Outcomes.All(Enum.IsDefined) ||
            trigger.Outcomes.Distinct().Count() != trigger.Outcomes.Count || !Enum.IsDefined(trigger.Scope))
            return false;
        var ids = trigger.DefinitionIds ?? [];
        var references = trigger.DefinitionReferences ?? [];
        if (ids.Count > FlowGraphV2Limits.MaximumSelectedDefinitions || references.Count > FlowGraphV2Limits.MaximumSelectedDefinitions ||
            ids.Any(id => !Text(id, 128)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count ||
            references.Any(reference => reference is null || !trigger.Sources.Contains(reference.SourceKind) || !Text(reference.Id, 128)) ||
            references.Where(reference => reference is not null).Select(reference => (reference.SourceKind, reference.Id)).Distinct().Count() != references.Count)
            return false;
        if (trigger.Scope == FlowExecutionScope.AllInTenant ? ids.Count != 0 || references.Count != 0 :
            ids.Count == 0 && references.Count == 0 || ids.Count > 0 && (references.Count > 0 || trigger.Sources.Count != 1))
            return false;
        return (trigger.AgentIds is null || trigger.AgentIds.Count <= FlowGraphV2Limits.MaximumSelectedDefinitions &&
                trigger.AgentIds.All(id => id != Guid.Empty) && trigger.AgentIds.Distinct().Count() == trigger.AgentIds.Count) &&
            (trigger.RequestKinds is null || trigger.Sources.Contains(FlowExecutionSourceKind.Request) &&
                trigger.RequestKinds.Count <= 32 && trigger.RequestKinds.All(kind => Text(kind, 128)) &&
                trigger.RequestKinds.Distinct(StringComparer.Ordinal).Count() == trigger.RequestKinds.Count);
    }

    private static bool ValidDefinition(FlowExecutionDefinitionDto? definition) => definition is not null &&
        Text(definition.Id, 128) && (definition.Name is null || Text(definition.Name, 128)) &&
        (definition.Revision is null || Text(definition.Revision, 128)) &&
        (definition.ContentHash is null || Hash(definition.ContentHash));
    private static bool ValidBinding(FlowInputBindingDto? binding)
    {
        if (binding is null || !Enum.IsDefined(binding.Source) || !Enum.IsDefined(binding.DataType)) return false;
        return binding.Source switch
        {
            FlowInputSource.Event => Path(binding.Path) && binding.NodeId is null && binding.Literal is null && binding.DataType != FlowDataType.SecretReference,
            FlowInputSource.NodeOutput => Path(binding.Path) && binding.NodeId is { } id && id != Guid.Empty && binding.Literal is null && binding.DataType != FlowDataType.SecretReference,
            FlowInputSource.SecretReference => Text(binding.Path, 256) && binding.NodeId is null && binding.Literal is null && binding.DataType == FlowDataType.SecretReference,
            FlowInputSource.Literal => binding.Path is null && binding.NodeId is null && binding.Literal is { } value &&
                value.ValueKind != JsonValueKind.Undefined && binding.DataType != FlowDataType.SecretReference &&
                Encoding.UTF8.GetByteCount(value.GetRawText()) <= FlowGraphV2Limits.MaximumBindingBytes && JsonType(value) == binding.DataType,
            _ => false
        };
    }

    private static bool ValidPredicateValue(FlowPredicateDto predicate) =>
        predicate.Input is not null && JsonType(predicate.Value) == predicate.Input.DataType &&
        predicate.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False &&
        Encoding.UTF8.GetByteCount(predicate.Value.GetRawText()) <= FlowGraphV2Limits.MaximumBindingBytes &&
        (predicate.Input.DataType == FlowDataType.Number ||
            predicate.Comparison is FlowComparison.Equal or FlowComparison.NotEqual);
    private static FlowDataType? JsonType(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => FlowDataType.Text,
        JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => FlowDataType.Number,
        JsonValueKind.True or JsonValueKind.False => FlowDataType.Boolean,
        JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array => FlowDataType.Json,
        _ => null
    };

    private static FlowDataType? EventType(string path, bool? execution) => FlowBindingCatalogue.TypeForEvent(path, execution);
    private static FlowDataType? OutputType(FlowNodeKind kind, string path) => FlowBindingCatalogue.TypeForOutput(kind, path);

    private static Dictionary<Guid, HashSet<Guid>> Dominators(IEnumerable<Guid> ids,
        IReadOnlyDictionary<Guid, List<Guid>> incoming, Guid? triggerId)
    {
        var all = ids.ToHashSet();
        var result = all.ToDictionary(id => id, id => id == triggerId ? new HashSet<Guid> { id } : new HashSet<Guid>(all));
        for (var pass = 0; pass < all.Count; pass++)
        {
            var changed = false;
            foreach (var id in all.Where(id => id != triggerId))
            {
                var predecessors = incoming[id];
                var next = predecessors.Count == 0 ? new HashSet<Guid>() : new HashSet<Guid>(result[predecessors[0]]);
                foreach (var predecessor in predecessors.Skip(1)) next.IntersectWith(result[predecessor]);
                next.Add(id);
                if (!next.SetEquals(result[id])) { result[id] = next; changed = true; }
            }
            if (!changed) break;
        }
        return result;
    }

    private static bool Text(string? value, int maximum) => FlowGraphValidator.IsBoundedText(value, maximum);
    private static bool Identifier(string? value) => Text(value, 64) && value!.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static bool Path(string? value) => Text(value, FlowGraphV2Limits.MaximumPathLength) &&
        value!.Split('.').Length <= 8 && value.Split('.').All(Identifier);
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool Coordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= 20000;
    private static bool ValidViewport(FlowViewportDto viewport) => double.IsFinite(viewport.Scale) && viewport.Scale is >= .1 and <= 10 &&
        new[] { viewport.ScrollX, viewport.ScrollY, viewport.NegativeX, viewport.NegativeY, viewport.PositiveX, viewport.PositiveY }.All(Coordinate);
    private static T? SingleOrDefaultSafe<T>(this IReadOnlyList<T> values) where T : class => values.Count == 1 ? values[0] : null;
}

using System.Globalization;
using System.Text;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>Pure DTO evaluation for dry run. No runtime, connector or persisted execution seam is referenced.</summary>
public static class FlowPureEvaluation
{
    public static FlowDryRunResultDto DryRun(FlowDryRunRequest request)
    {
        var validation = FlowGraphValidator.ValidateComplete(request.Graph);
        if (!validation.Valid || !ValidEventData(request.Input)) return new(false, false, "invalid-draft", null, [], validation.Issues);
        var nodes = request.Graph.Nodes.ToDictionary(node => node.Id);
        var edges = request.Graph.Edges.ToDictionary(edge => edge.SourceNodeId, edge => edge.TargetNodeId);
        var current = request.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.AlertRaised);
        var visited = new List<Guid>(); FlowIncidentFieldsDto? fields = null;
        while (true)
        {
            visited.Add(current.Id);
            if (current.Kind == FlowNodeKind.Condition && !Matches(current.Condition!, request.Input)) return new(true, true, "condition-false", null, visited, []);
            if (current.Kind == FlowNodeKind.MapIncident) fields = Map(current.Mapping!, request.Input);
            if (!edges.TryGetValue(current.Id, out var next)) break;
            current = nodes[next];
        }
        return fields is null ? new(false, false, "mapping-required", null, visited, []) : new(true, false, "inert-preview", fields, visited, []);
    }
    public static bool ValidEventData(FlowEventDataDto? data) => data is not null && data.AgentId != Guid.Empty && data.RuleId != Guid.Empty &&
        new[] { data.RuleName, data.ClientName, data.Resource, data.Metric, data.Severity }.All(value => FlowGraphValidator.IsBoundedText(value, FlowLimits.MaximumEventTextLength)) &&
        (data.ServiceState is null || FlowGraphValidator.IsBoundedText(data.ServiceState, 128)) &&
        (data.NumericValue is null || double.IsFinite(data.NumericValue.Value)) && data.ObservedAtUtc != default;

    public static bool Matches(FlowConditionDto condition, FlowEventDataDto input)
    {
        if (condition.Field == FlowValueField.NumericValue)
        {
            if (input.NumericValue is not { } actual || condition.NumericValue is not { } expected) return false;
            return condition.Comparison switch { FlowComparison.Equal => actual == expected, FlowComparison.NotEqual => actual != expected,
                FlowComparison.GreaterThan => actual > expected, FlowComparison.GreaterThanOrEqual => actual >= expected,
                FlowComparison.LessThan => actual < expected, FlowComparison.LessThanOrEqual => actual <= expected, _ => false };
        }
        var text = condition.Field switch { FlowValueField.Severity => input.Severity, FlowValueField.Metric => input.Metric,
            FlowValueField.Resource => input.Resource, FlowValueField.RuleName => input.RuleName, FlowValueField.ClientName => input.ClientName,
            FlowValueField.ServiceState => input.ServiceState, _ => null };
        if (text is null) return false;
        var equal = string.Equals(text, condition.TextValue, StringComparison.Ordinal);
        return condition.Comparison == FlowComparison.Equal ? equal : condition.Comparison == FlowComparison.NotEqual && !equal;
    }
    public static FlowIncidentFieldsDto Map(FlowIncidentMappingDto mapping, FlowEventDataDto input)
    {
        var title = Expand(mapping.TitleTemplate, input); var description = Expand(mapping.DescriptionTemplate, input);
        if (!FlowGraphValidator.IsBoundedText(title, FlowLimits.MaximumTitleLength) || !FlowGraphValidator.IsBoundedText(description, FlowLimits.MaximumDescriptionLength, multiline: true))
            throw new InvalidOperationException("mapping-output-limit");
        return new(title, description, mapping.Priority);
    }
    private static string Expand(string template, FlowEventDataDto input)
    {
        var output = new StringBuilder();
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] != '{') { output.Append(template[index]); continue; }
            var end = template.IndexOf('}', index + 1);
            if (end < 0) throw new InvalidOperationException("mapping-template");
            output.Append(template[(index + 1)..end] switch { "ruleName" => input.RuleName, "clientName" => input.ClientName, "resource" => input.Resource,
                "metric" => input.Metric, "severity" => input.Severity, "value" => input.NumericValue?.ToString("R", CultureInfo.InvariantCulture) ?? "Unknown",
                "serviceState" => input.ServiceState ?? "Unknown", "occurredAt" => input.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture), _ => throw new InvalidOperationException("mapping-template") });
            index = end;
        }
        return output.ToString();
    }
}

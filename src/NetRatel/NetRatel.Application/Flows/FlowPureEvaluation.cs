using System.Globalization;
using System.Text;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;

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
    public static bool ValidEventData(FlowEventDataDto? data) => data is not null && data.RuleId != Guid.Empty &&
        (data.AgentId != Guid.Empty || ValidOfflineSummary(data)) &&
        (data.OfflineSummary is null || ValidOfflineSummary(data)) &&
        new[] { data.RuleName, data.ClientName, data.Resource, data.Metric, data.Severity }.All(value => FlowGraphValidator.IsBoundedText(value, FlowLimits.MaximumEventTextLength)) &&
        (data.ServiceState is null || FlowGraphValidator.IsBoundedText(data.ServiceState, 128)) &&
        (data.NumericValue is null || double.IsFinite(data.NumericValue.Value)) && data.ObservedAtUtc != default;

    private static bool ValidOfflineSummary(FlowEventDataDto data) => data.Resource == MonitoringOfflinePresentation.ResourceKey &&
        data.Metric == "ClientOffline" && MonitoringOfflinePresentation.IsValid(data.OfflineSummary);

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
    public static FlowIncidentFieldsDto Map(FlowIncidentMappingDto mapping, FlowEventDataDto input, FlowAiResultDto? ai = null)
    {
        var title = Expand(mapping.TitleTemplate, input, ai); var description = Expand(mapping.DescriptionTemplate, input, ai);
        if (input.OfflineSummary is { } offline && !description.Contains(offline.Summary, StringComparison.Ordinal))
            description += "\n\n" + offline.Summary;
        if (ai is not null)
        {
            var section = "\n\nAI investigation\nOutcome: " + ai.Status + "\nObserved: " + ai.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture) +
                (ai.InvestigationId == Guid.Empty ? "\nInvestigation: Not started" : "\nInvestigation: /ai/ops?tenantId=" + ai.TenantId + "&investigation=" + ai.InvestigationId.ToString("D")) + "\nTurn: " + ai.MessageId.ToString("D");
            var findings = "";
            if (ai.Status == FlowAiResultStatus.Completed)
            {
                if (!string.IsNullOrWhiteSpace(ai.Summary)) findings += "\nSummary: " + ai.Summary;
                if (!string.IsNullOrWhiteSpace(ai.Evidence)) findings += "\nEvidence: " + ai.Evidence;
                if (!string.IsNullOrWhiteSpace(ai.Recommendations)) findings += "\nRecommendations: " + ai.Recommendations;
            }
            else findings += "\nNo successful AI findings were confirmed. The original monitoring alert is preserved above.";
            var available = FlowLimits.MaximumDescriptionLength - description.Length;
            if (available < section.Length + 80)
            {
                // An existing mapping can already use the receiver's whole description budget. Preserve every
                // measured fact separately and retain a labelled excerpt of that mapping so inserting AI still delivers.
                var original = description;
                var facts = "Original alert facts (measured monitoring data)\nTenant ID: " + ai.TenantId +
                    "\nRule ID: " + input.RuleId.ToString("D") + "\nAgent ID: " + input.AgentId.ToString("D") +
                    "\nRule: " + input.RuleName + "\nClient: " + input.ClientName + "\nResource: " + input.Resource +
                    "\nMetric: " + input.Metric + "\nSeverity: " + input.Severity +
                    "\nValue: " + (input.NumericValue?.ToString("R", CultureInfo.InvariantCulture) ?? "Unknown") +
                    "\nService: " + (input.ServiceState ?? "Unknown") + "\nObserved: " + input.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture);
                if (input.OfflineSummary is { } summary)
                    facts += "\nOffline count: " + summary.OfflineCount + "\nMonitored count: " + summary.MonitoredCount +
                        "\nSuppressed count: " + summary.SuppressedCount + "\nOffline clients: " + (summary.Truncated ? "Names omitted in the original snapshot" : summary.ClientNames);
                const string excerptLabel = "\n\nOriginal mapped description (excerpt):\n";
                const string retained = "\n[Full original alert and mapping retained in flow history.]";
                var cut = Math.Min(1024, Math.Max(0, FlowLimits.MaximumDescriptionLength - facts.Length - excerptLabel.Length - retained.Length - section.Length - 80));
                if (cut > 0 && char.IsHighSurrogate(original[cut - 1])) cut--;
                description = facts + excerptLabel + original[..cut] + retained;
                available = FlowLimits.MaximumDescriptionLength - description.Length;
            }
            if (section.Length + findings.Length > available)
            {
                const string omitted = "\n[Additional AI findings omitted to fit incident description.]";
                var cut = Math.Max(0, available - section.Length - omitted.Length);
                if (cut > 0 && char.IsHighSurrogate(findings[cut - 1])) cut--;
                findings = findings[..cut] + omitted;
            }
            description += section + findings;
        }
        if (!FlowGraphValidator.IsBoundedText(title, FlowLimits.MaximumTitleLength) || !FlowGraphValidator.IsBoundedText(description, FlowLimits.MaximumDescriptionLength, multiline: true))
            throw new InvalidOperationException("mapping-output-limit");
        return new(title, description, mapping.Priority);
    }
    private static string Expand(string template, FlowEventDataDto input, FlowAiResultDto? ai = null)
    {
        var output = new StringBuilder();
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] != '{') { output.Append(template[index]); continue; }
            var end = template.IndexOf('}', index + 1);
            if (end < 0) throw new InvalidOperationException("mapping-template");
            output.Append(template[(index + 1)..end] switch { "ruleName" => input.RuleName, "clientName" => input.ClientName, "resource" => input.Resource,
                "metric" => input.Metric, "severity" => input.Severity, "value" => input.NumericValue?.ToString("R", CultureInfo.InvariantCulture) ?? "Unknown",
                "serviceState" => input.ServiceState ?? "Unknown", "occurredAt" => input.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                "offlineSummary" => input.OfflineSummary?.Summary ?? "", "offlineCount" => input.OfflineSummary is { } offline
                    ? (offline.OfflineCount - offline.SuppressedCount).ToString(CultureInfo.InvariantCulture) : "0",
                "offlineClients" => input.OfflineSummary?.ClientNames ?? "",
                "aiSummary" => ai?.Status == FlowAiResultStatus.Completed ? ai.Summary ?? "" : "",
                "aiEvidence" => ai?.Status == FlowAiResultStatus.Completed ? ai.Evidence ?? "" : "",
                "aiRecommendations" => ai?.Status == FlowAiResultStatus.Completed ? ai.Recommendations ?? "" : "",
                "aiStatus" => ai?.Status.ToString() ?? "Pending",
                "aiObservedAt" => ai?.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture) ?? "",
                "aiInvestigationId" => ai?.InvestigationId.ToString("D") ?? "", _ => throw new InvalidOperationException("mapping-template") });
            index = end;
        }
        return output.ToString();
    }
}

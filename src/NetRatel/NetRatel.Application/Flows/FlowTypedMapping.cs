using System.Text;
using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>Single-pass field mapping. Inserted values never become executable expressions or new placeholders.</summary>
public static class FlowTypedMapping
{
    public static FlowIncidentFieldsDto Map(FlowIncidentMappingDto mapping, FlowExecutionEventDto? execution,
        FlowEventDataDto? alert, IReadOnlyDictionary<string, JsonElement> inputs, FlowAiResultDto? ai = null,
        IReadOnlyDictionary<string, string>? simulatedAi = null)
    {
        if ((execution is null) == (alert is null)) throw new ArgumentException("Exactly one original source is required.");
        if (alert?.Presentation is not null && FlowMonitoringMappingFields.IsHistoricalStock(mapping))
            mapping = mapping with { TitleTemplate = FlowMonitoringMappingFields.Title, DescriptionTemplate = FlowMonitoringMappingFields.Description };
        var source = execution is null ? null : FlowExecutionEvidenceFormatter.Values(execution, ai);
        string Field(string name)
        {
            if (name.StartsWith("input.", StringComparison.Ordinal))
            {
                if (!inputs.TryGetValue(name[6..], out var value) || value.ValueKind == JsonValueKind.Undefined)
                    throw new InvalidOperationException("mapping-input-unavailable");
                return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            }
            if (simulatedAi is not null && (name is "aiSummary" or "aiEvidence" or "aiRecommendations" or
                "aiStatus" or "aiObservedAt" or "aiInvestigationId"))
                return simulatedAi.TryGetValue(name, out var sample) ? sample : throw new InvalidOperationException("mapping-input-unavailable");
            if (source is not null && source.TryGetValue(name, out var text)) return text;
            if (alert is not null) return FlowPureEvaluation.FieldValue(name, alert, ai);
            throw new InvalidOperationException("mapping-template");
        }
        var title = Expand(mapping.TitleTemplate, Field, FlowLimits.MaximumTitleLength);
        var description = Expand(mapping.DescriptionTemplate, Field, FlowLimits.MaximumDescriptionLength);
        if (!FlowGraphValidator.IsBoundedText(title, FlowLimits.MaximumTitleLength) ||
            !FlowGraphValidator.IsBoundedText(description, FlowLimits.MaximumDescriptionLength, multiline: true))
            throw new InvalidOperationException("mapping-output-limit");
        return new(title, description, mapping.Priority);
    }

    private static string Expand(string template, Func<string, string> field, int maximum)
    {
        var output = new StringBuilder();
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] == '}') throw new InvalidOperationException("mapping-template");
            if (template[index] != '{') output.Append(template[index]);
            else
            {
                var end = template.IndexOf('}', index + 1);
                if (end < 0) throw new InvalidOperationException("mapping-template");
                output.Append(field(template[(index + 1)..end]));
                index = end;
            }
            if (output.Length > maximum) throw new InvalidOperationException("mapping-output-limit");
        }
        return output.ToString();
    }
}

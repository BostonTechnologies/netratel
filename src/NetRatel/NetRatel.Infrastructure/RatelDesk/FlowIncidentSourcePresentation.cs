using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

/// <summary>Source-specific formatting only. Receiver preparation persists these exact bytes before the first send.</summary>
internal static class FlowIncidentSourcePresentation
{
    public static int Priority(FlowIncidentActionDraft draft, RatelDeskPriorityMapping mapping)
    {
        if (draft.Fields.Priority is { } priority) return priority;
        if (draft.Event.ExecutionEvent is { } execution)
            return execution.Outcome is FlowExecutionOutcome.Failed or FlowExecutionOutcome.TimedOut or FlowExecutionOutcome.Denied
                ? mapping.Error : mapping.Information;
        var data = draft.Event.Data ?? throw new ArgumentException("Monitoring alert data is required.");
        return data.Severity.ToLowerInvariant() switch
        {
            "critical" => mapping.Critical, "error" or "high" => mapping.Error,
            "warning" or "medium" => mapping.Warning, _ => mapping.Information
        };
    }

    public static string Description(FlowIncidentActionDraft draft)
    {
        if (draft.Event.ExecutionEvent is not null)
            return draft.Fields.Description + "\n\nNetRatel execution occurrence\n" +
                $"Flow run: {draft.RunId:D}\nVersion: {draft.Event.FlowVersionId:D}\n" +
                $"Source: {draft.SourceInstanceId:D}/{draft.Event.OccurrenceId:D}/{draft.Event.EventId:D}";
        var data = draft.Event.Data ?? throw new ArgumentException("Monitoring alert data is required.");
        // Preserve the exact historical monitoring suffix and severity mapping.
        return draft.Fields.Description + "\n\nNetRatel monitoring occurrence\n" +
            (data.OfflineSummary is null ? $"Client: {data.ClientName} ({data.AgentId:D})\n" :
                $"Scope: {data.ClientName}\n") + $"Resource: {data.Resource}\n" +
            $"Rule: {data.RuleName} ({data.RuleId:D})\nSeverity: {data.Severity}\n" +
            $"Metric: {data.Metric}\nObserved value: {data.NumericValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? data.ServiceState ?? "Unknown"}\n" +
            $"Observed at: {data.ObservedAtUtc:O}\nOccurred at: {draft.Event.OccurredAtUtc:O}\n" +
            $"Source: {draft.SourceInstanceId:D}/{draft.Event.OccurrenceId:D}/{draft.Event.EventId:D}";
    }
}

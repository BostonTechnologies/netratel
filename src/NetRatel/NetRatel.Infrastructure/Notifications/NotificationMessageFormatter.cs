using System.Globalization;
using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Notifications;

namespace NetRatel.Infrastructure.Notifications;

public static class NotificationMessageFormatter
{
    public static string Format(FlowIncidentActionDraft draft)
    {
        if (draft.Event.ExecutionEvent is { } execution)
        {
            var values = FlowExecutionEvidenceFormatter.Values(execution);
            var text = $"{Bound(draft.Fields.Title, 200)}\n{Bound(draft.Fields.Description, 1000)}\n\n" +
                $"Source: {values["sourceKind"]}\nDefinition: {values["definitionName"]}\nOutcome: {values["outcome"]}\n" +
                $"Execution: {execution.ExecutionId}\nAttempt: {execution.AttemptId}\nTarget: {values["targetName"]}\n" +
                $"Occurred: {execution.OccurredAtUtc:yyyy-MM-dd HH:mm:ss} UTC\nEvent: {execution.EventId:D}";
            return Bound(NetRatel.Shared.Security.OperatorOutputRedactor.Redact(text), NotificationConnectorLimits.MaximumMessageLength);
        }
        var data = draft.Event.Data ?? throw new ArgumentException("Monitoring alert data is required.");
        if (data.Presentation is not null)
        {
            var human = $"{Bound(draft.Fields.Title, 200)}\n{Bound(draft.Fields.Description, 1400)}\n\n" +
                $"Alert: {draft.Event.OccurrenceId:D}\nRule ID: {data.RuleId:D}";
            // Slack is sent with mrkdwn=false and context escaping by the transport. Discord uses Markdown,
            // so render this new plain-text presentation literally; transport also disables all mentions.
            if (draft.Kind == NetRatel.Shared.Contracts.Flows.FlowActionKind.Discord)
                human = string.Concat(human.SelectMany(c => "\\`*_~[]()>#|".Contains(c) ? new[] { '\\', c } : new[] { c }));
            return Bound(NetRatel.Shared.Security.OperatorOutputRedactor.Redact(human), NotificationConnectorLimits.MaximumMessageLength);
        }
        var description = data.OfflineSummary?.Summary ?? draft.Fields.Description;
        var message = $"{Bound(draft.Fields.Title, 200)}\n{Bound(description, 1000)}\n\n" +
            $"Rule: {Bound(data.RuleName, 120)}\nSeverity: {Bound(data.Severity, 32)}\n";
        if (data.OfflineSummary is null)
            message += $"Client: {Bound(data.ClientName, 100)}\nResource: {Bound(data.Resource, 100)}\n" +
                $"Observed: {Bound(data.NumericValue?.ToString(CultureInfo.InvariantCulture) ?? data.ServiceState ?? "Unknown", 40)}\n";
        message += $"Occurred: {draft.Event.OccurredAtUtc:yyyy-MM-dd HH:mm:ss} UTC\nAlert: {draft.Event.OccurrenceId:D}";
        return Bound(message, NotificationConnectorLimits.MaximumMessageLength);
    }

    private static string Bound(string text, int limit)
    {
        var safe = string.Concat(text.Where(c => !char.IsControl(c) || c == '\n')).Trim();
        if (safe.Length <= limit) return safe;
        var length = limit - 1;
        if (length > 0 && char.IsHighSurrogate(safe[length - 1])) length--;
        return safe[..length] + "…";
    }
}

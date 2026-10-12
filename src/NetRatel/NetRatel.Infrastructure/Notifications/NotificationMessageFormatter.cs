using System.Globalization;
using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Notifications;

namespace NetRatel.Infrastructure.Notifications;

public static class NotificationMessageFormatter
{
    public static string Format(FlowIncidentActionDraft draft)
    {
        var data = draft.Event.Data;
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

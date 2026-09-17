namespace NetRatel.Application.Notifications;

public sealed record NetRatelNotificationSummaryDto
{
    public int UnreadCount { get; init; }
    public int UnreadErrorCount { get; init; }
    public int TotalCount { get; init; }
}

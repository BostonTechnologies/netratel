namespace NetRatel.Application.Notifications;

public sealed record NetRatelNotificationDto
{
    public Guid Id { get; init; }
    public string EventType { get; init; } = string.Empty;
    public DateTimeOffset OccurredUtc { get; init; }
    public string Source { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string? TenantId { get; init; }
    public string? EntityId { get; init; }
    public NetRatelNotificationSeverity Severity { get; init; } = NetRatelNotificationSeverity.Info;
    public string? Message { get; init; }
    public string PayloadJson { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool IsRead { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public string? LastError { get; init; }
}

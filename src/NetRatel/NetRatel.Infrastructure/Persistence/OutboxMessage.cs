namespace NetRatel.Infrastructure.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredUtc { get; set; }

    public string Type { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string? EntityId { get; set; }
    public string? Severity { get; set; }
    public string? Message { get; set; }

    public string Status { get; set; } = OutboxStatuses.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }

    public DateTimeOffset? LockedUntilUtc { get; set; }
    public string? LockOwner { get; set; }

    public string? LastError { get; set; }
}

public static class OutboxStatuses
{
    public const string Pending = "Pending";
    public const string Published = "Published";
    public const string Failed = "Failed";
    public const string Disabled = "Disabled";
}

namespace NetRatel.Application.Notifications;

public sealed record OutboxEnvelope(
    Guid Id,
    DateTimeOffset OccurredUtc,
    string Type,
    string PayloadJson,
    string Source,
    string CorrelationId,
    string? TenantId,
    string? EntityId,
    string? Severity,
    string? Message,
    string Status,
    int Attempts,
    DateTimeOffset? NextAttemptUtc,
    DateTimeOffset? LockedUntilUtc,
    string? LockOwner,
    string? LastError);

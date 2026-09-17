namespace NetRatel.Application.Events;

public sealed record DomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredUtc { get; init; } = DateTimeOffset.UtcNow;

    public required string EventType { get; init; }
    public required string Source { get; init; }
    public required string CorrelationId { get; init; }

    public string? TenantId { get; init; }
    public string? EntityId { get; init; }
    public string? Severity { get; init; }
    public string? Message { get; init; }
    public object? Payload { get; init; }
}

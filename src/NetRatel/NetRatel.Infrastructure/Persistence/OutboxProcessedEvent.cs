namespace NetRatel.Infrastructure.Persistence;

public sealed class OutboxProcessedEvent
{
    public Guid EventId { get; set; }
    public string ConsumerName { get; set; } = string.Empty;
    public DateTimeOffset ProcessedUtc { get; set; }
}

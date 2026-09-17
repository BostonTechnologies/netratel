namespace NetRatel.Infrastructure.Persistence;

public sealed class OutboxReadReceipt
{
    public Guid EventId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset ReadUtc { get; set; }
}

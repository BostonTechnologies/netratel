namespace NetRatel.Infrastructure.Persistence;

public sealed class JobTaskLogRecord
{
    public long Id { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public long? JobTaskActivityId { get; set; }
    public string ClientIdentity { get; set; } = string.Empty;
    public int? TenantId { get; set; }
    public string Stream { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public JobTaskActivityRecord? Activity { get; set; }
}

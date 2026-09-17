namespace NetRatel.Infrastructure.Persistence;

public sealed class JobTaskActivityRecord
{
    public long Id { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public long? JobRunId { get; set; }
    public long? JobStepId { get; set; }
    public string ClientIdentity { get; set; } = string.Empty;
    public int? TenantId { get; set; }
    public Guid? AgentId { get; set; }
    // Retained only to display historical activity that predate Agent targeting.
    public string TaskType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public JobRunRecord? JobRun { get; set; }
    public JobStepDefinition? JobStep { get; set; }
    public List<JobTaskLogRecord> Logs { get; set; } = [];
}

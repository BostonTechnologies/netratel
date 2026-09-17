namespace NetRatel.Infrastructure.Persistence;

public sealed class JobRunRecord
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public int? TenantId { get; set; }
    public Guid? AgentId { get; set; }
    // Retained only to display historical runs that predate Agent targeting.
    public string ClientIdentity { get; set; } = string.Empty;
    public string StartedBy { get; set; } = string.Empty;
    public int Status { get; set; }
    public int CurrentStepOrdinal { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? Error { get; set; }
    public string? InputsJson { get; set; }
    public string? OptionsJson { get; set; }
    public JobDefinition? Job { get; set; }
    public List<JobStepRunRecord> Steps { get; set; } = [];
    public List<JobTaskActivityRecord> Activities { get; set; } = [];
}

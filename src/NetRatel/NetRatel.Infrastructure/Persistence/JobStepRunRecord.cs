namespace NetRatel.Infrastructure.Persistence;

public sealed class JobStepRunRecord
{
    public long Id { get; set; }
    public long JobRunId { get; set; }
    public long? JobStepId { get; set; }
    public int Status { get; set; }
    public int Ordinal { get; set; }
    public string? TaskRequestId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public JobRunRecord? JobRun { get; set; }
    public JobStepDefinition? JobStep { get; set; }
}

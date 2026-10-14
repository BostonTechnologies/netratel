using NetRatel.Shared;

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
    // Captured when dispatched. Historical records retain null where evidence is absent.
    public ClientEnvironment? Environment { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public int? ExitCode { get; set; }
    public string? TargetDisplayName { get; set; }
    public string? TargetHostName { get; set; }
    public string? TargetName { get; set; }
    public string? ScriptName { get; set; }
    public string? ScriptType { get; set; }
    public string? CommandLabel { get; set; }
    public string? ScriptContentHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public JobRunRecord? JobRun { get; set; }
    public JobStepDefinition? JobStep { get; set; }
    public List<JobTaskLogRecord> Logs { get; set; } = [];
}

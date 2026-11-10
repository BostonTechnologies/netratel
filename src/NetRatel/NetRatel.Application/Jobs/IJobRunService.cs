using NetRatel.Shared;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.Application.Jobs;

public enum JobRunState
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    TimedOut = 5,
    Unknown = 6
}

public enum JobStepRunState
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
    Cancelled = 5,
    TimedOut = 6,
    Unknown = 7
}

public sealed record JobRunInfo(
    ulong Id,
    ulong JobId,
    int? TenantId,
    string ClientIdentity,
    string StartedBy,
    JobRunState Status,
    int CurrentStepOrdinal,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Error,
    string? InputsJson,
    string? OptionsJson,
    Guid? AgentId = null,
    NetRatel.Shared.ClientEnvironment? Environment = null,
    string? TargetDisplayName = null,
    string? TargetHostName = null,
    string? TargetName = null,
    string? InputProtectionStatus = null,
    string? SubmittedDefinitionRevision = null)
{
    public bool IsAggregate { get; init; }
    public ulong? ParentRunId { get; init; }
    public string? TargetPlanDigest { get; init; }
    public string? DefinitionDigest { get; init; }
    public JobTargetSource? TargetSource { get; init; }
    public JobExecutionPolicy? ExecutionPolicy { get; init; }
    public int TargetCount { get; init; }
    public int EffectiveConcurrency { get; init; }
    public JobRunNodeCountsDto? NodeCounts { get; init; }
}


public sealed record JobStepRunInfo(
    ulong Id,
    ulong JobRunId,
    ulong? JobStepId,
    JobStepRunState Status,
    int Ordinal,
    string? TaskRequestId,
    string? Error,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record JobTaskActivityInfo(
    ulong Id,
    string RequestId,
    ulong? JobRunId,
    ulong? JobStepId,
    string ClientIdentity,
    int? TenantId,
    string TaskType,
    string Status,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    Guid? AgentId = null,
    string? ResultJson = null,
    ClientEnvironment? Environment = null,
    DateTimeOffset? StartedAtUtc = null,
    int? ExitCode = null,
    string? TargetDisplayName = null,
    string? TargetHostName = null,
    string? TargetName = null,
    string? ScriptName = null,
    string? ScriptType = null,
    string? CommandLabel = null,
    string? ScriptContentHash = null);

public sealed record JobTaskLogInfo(
    long Id,
    string RequestId,
    ulong? JobTaskActivityId,
    string ClientIdentity,
    int? TenantId,
    string Stream,
    string Message,
    long Sequence,
    DateTimeOffset TimestampUtc);

public sealed record UpsertJobRunCommand(
    ulong RunId,
    ulong JobId,
    int? TenantId,
    string ClientIdentity,
    string StartedBy,
    JobRunState Status,
    int CurrentStepOrdinal,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Error,
    string? InputsJson,
    string? OptionsJson,
    Guid? AgentId = null,
    NetRatel.Shared.ClientEnvironment? Environment = null,
    string? TargetDisplayName = null,
    string? TargetHostName = null,
    string? TargetName = null)
{
    // Server-owned admission material, never bound from a public RunJobRequest.
    public bool? IsAggregate { get; init; }
    public JobResolvedTargetPlan? TargetPlan { get; init; }
}

public sealed record UpsertJobStepRunCommand(
    ulong StepRunId,
    ulong JobRunId,
    ulong JobStepId,
    JobStepRunState Status,
    int Ordinal,
    string? TaskRequestId,
    string? Error,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record UpsertJobTaskActivityCommand(
    ulong ActivityId,
    string RequestId,
    ulong? JobRunId,
    ulong? JobStepId,
    string ClientIdentity,
    int? TenantId,
    string TaskType,
    string Status,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    Guid? AgentId = null,
    string? ResultJson = null,
    ClientEnvironment? Environment = null,
    DateTimeOffset? StartedAtUtc = null,
    int? ExitCode = null,
    string? TargetDisplayName = null,
    string? TargetHostName = null,
    string? TargetName = null,
    string? ScriptName = null,
    string? ScriptType = null,
    string? CommandLabel = null,
    string? ScriptContentHash = null,
    TaskOutputPolicy? OutputPolicy = null);

public sealed record CreateJobTaskActivityCommand(
    string RequestId,
    ulong? JobRunId,
    ulong? JobStepId,
    string ClientIdentity,
    int? TenantId,
    string TaskType,
    string Status,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    Guid? AgentId = null,
    string? ResultJson = null,
    ClientEnvironment? Environment = null,
    DateTimeOffset? StartedAtUtc = null,
    int? ExitCode = null,
    string? TargetDisplayName = null,
    string? TargetHostName = null,
    string? TargetName = null,
    string? ScriptName = null,
    string? ScriptType = null,
    string? CommandLabel = null,
    string? ScriptContentHash = null,
    TaskOutputPolicy? OutputPolicy = null);

public sealed record AppendJobTaskLogCommand(
    string RequestId,
    ulong? JobTaskActivityId,
    string ClientIdentity,
    int? TenantId,
    string Stream,
    string Message,
    long Sequence,
    DateTimeOffset TimestampUtc,
    string? OutputProtectionBinding = null);

public sealed record UpdateJobTaskActivityStatusCommand(
    string RequestId,
    string Status,
    string? Error,
    DateTimeOffset? CompletedAtUtc,
    string? ResultJson = null,
    DateTimeOffset? StartedAtUtc = null,
    int? ExitCode = null);

public sealed record JobRunDetails(
    JobRunInfo Run,
    IReadOnlyList<JobStepRunInfo> Steps,
    IReadOnlyList<JobTaskActivityInfo> Activities);

public interface IJobRunService
{
    void BindNodeExecutionContext(ulong runId, JobTargetNode node) { }
    Task<JobTargetNode?> GetNodeExecutionContextAsync(ulong runId, CancellationToken ct = default)
        => Task.FromResult<JobTargetNode?>(null);
    async Task<ExecutionLogDto> GetForwardLogsPageAsync(string requestId, string? cursor, int limit,
        int maximumBytes, CancellationToken ct = default)
        => JobLogPageBuilder.Build(requestId, await GetLogsByRequestIdAsync(requestId, ct), cursor, limit, maximumBytes);
    Task<JobResolvedTargetPlan?> GetResolvedTargetPlanAsync(ulong runId, CancellationToken ct = default)
        => Task.FromResult<JobResolvedTargetPlan?>(null);
    async Task<IReadOnlyList<JobTaskLogInfo>> GetLogsPageAsync(string requestId, long afterSequence,
        int pageSize, CancellationToken ct = default)
        => (await GetLogsByRequestIdAsync(requestId, ct)).Where(row => row.Sequence > afterSequence)
            .OrderBy(row => row.Sequence).ThenBy(row => row.Id).Take(Math.Clamp(pageSize, 1, 1000)).ToArray();
    // Agent lifecycle ingress: protect output before creating any diagnostic copy.
    Task<string?> ProtectTaskOutputAsync(string requestId, string? output, CancellationToken ct = default)
        => Task.FromResult(output);
    Task<TaskOutputPolicy> GetRunOutputPolicyAsync(ulong runId, CancellationToken ct = default)
        => Task.FromResult(TaskOutputPolicy.Create([]));
    // Called by the server authority immediately after loading the submitted definition;
    // never populated from RunJobRequest or any caller-supplied sensitivity flags.
    void BindSubmittedDefinition(ulong runId, JobDefinitionDetails definition) { }
    void BindPinnedExecutionSnapshot(ulong runId, PinnedJobExecutionSnapshot snapshot)
        => throw new NotSupportedException("Pinned canonical job snapshots are unavailable.");
    Task<PinnedJobExecutionSnapshot?> GetPinnedExecutionSnapshotAsync(ulong runId, CancellationToken ct = default)
        => Task.FromResult<PinnedJobExecutionSnapshot?>(null);
    void BindMcpExecutionSnapshot(ulong runId, McpOperatorJobExecutionSnapshot snapshot)
        => throw new NotSupportedException("Reviewed MCP job execution snapshots are unavailable.");
    Task<McpOperatorJobExecutionSnapshot?> GetMcpExecutionSnapshotAsync(ulong runId, CancellationToken ct = default)
        => Task.FromResult<McpOperatorJobExecutionSnapshot?>(null);
    // Internal execution boundary only. Never map this material into ordinary responses.
    Task<string?> GetExecutionOptionsAsync(ulong runId, CancellationToken ct = default)
        => throw new NotSupportedException("Protected execution options retrieval is unavailable.");
    Task<string?> GetExecutionInputsAsync(ulong runId, CancellationToken ct = default)
        => throw new NotSupportedException("Protected execution input retrieval is unavailable.");
    Task<IReadOnlyList<JobRunInfo>> ListAsync(CancellationToken ct = default);
    Task<JobRunInfo?> GetAsync(ulong runId, CancellationToken ct = default);
    Task<JobRunDetails?> GetDetailsAsync(ulong runId, CancellationToken ct = default);
    Task DeleteAsync(ulong runId, CancellationToken ct = default);
    Task<JobTaskActivityInfo?> GetActivityByIdAsync(ulong activityId, CancellationToken ct = default);
    Task<JobTaskActivityInfo?> GetActivityByRequestIdAsync(string requestId, CancellationToken ct = default);
    Task<IReadOnlyList<JobTaskActivityInfo>> ListTaskActivitiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<JobTaskLogInfo>> GetLogsByRequestIdAsync(string requestId, CancellationToken ct = default);
    Task<JobRunInfo> UpsertRunAsync(UpsertJobRunCommand command, CancellationToken ct = default);
    Task<JobStepRunInfo> UpsertStepRunAsync(UpsertJobStepRunCommand command, CancellationToken ct = default);
    Task<JobTaskActivityInfo> CreateTaskActivityAsync(CreateJobTaskActivityCommand command, CancellationToken ct = default);
    Task<JobTaskActivityInfo> UpsertTaskActivityAsync(UpsertJobTaskActivityCommand command, CancellationToken ct = default);
    Task<JobTaskLogInfo> AppendTaskLogAsync(AppendJobTaskLogCommand command, CancellationToken ct = default);
    Task<JobTaskActivityInfo?> UpdateTaskActivityStatusAsync(UpdateJobTaskActivityStatusCommand command, CancellationToken ct = default);
}

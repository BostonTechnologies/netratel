using System.Collections.Generic;
using NetRatel.Shared;

namespace NetRatel.Shared.Contracts.Jobs;

public enum JobRunStatusDto
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    TimedOut = 5
}

public enum JobStepRunStatusDto
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4
}

public sealed record JobRunOptionDto(string Name, string Value, string? Source = null);

public record JobRunDto(
    ulong Id,
    ulong JobId,
    string JobName,
    int? TenantId,
    string ClientIdentity,
    string StartedBy,
    JobRunStatusDto Status,
    int CurrentStepOrdinal,
    long CreatedAtMicros,
    long? StartedAtMicros,
    long? CompletedAtMicros,
    string? Error,
    string? InputsJson,
    IReadOnlyList<JobRunOptionDto>? Options = null,
    string? TenantDisplayName = null,
    string? ClientDisplayName = null,
    ClientEnvironment Environment = ClientEnvironment.None,
    Guid? AgentId = null
);

public record JobStepRunDto(
    ulong Id,
    ulong JobRunId,
    ulong? JobStepId,
    JobStepRunStatusDto Status,
    int Ordinal,
    string? TaskRequestId,
    long? StartedAtMicros,
    long? CompletedAtMicros,
    string? Error,
    string? LogText = null
);

public sealed record PagedJobRunsDto(
    IReadOnlyList<JobRunDto> Items,
    int TotalCount);

/// <summary>
/// Request to run a job. InputsJson is the resolved Job inputs (e.g. map of JobParam.Name → value).
/// ClientIdentityOverride allows directing to a specific client; otherwise Job.ClientIdentity is used.
/// </summary>
public record RunJobRequest(
    string StartedBy,
    string? InputsJson,
    string? ClientIdentityOverride,
    int? ExpectedRuntimeSeconds = null,
    int? GraceSeconds = null,
    int? HardTimeoutSeconds = null
);

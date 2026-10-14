using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.Shared.Contracts.Jobs;

public sealed record JobStepRunLogsDto(int Ordinal, IReadOnlyList<TaskLogDto> Logs, bool Truncated = false);

/// <summary>A bounded authoritative replacement, including recovery after missed invalidations.</summary>
public sealed record JobRunStreamSnapshotDto(JobRunDto Run, IReadOnlyList<JobStepRunDto> Steps,
    IReadOnlyList<JobStepRunLogsDto> Logs);

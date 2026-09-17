using System.Collections.Generic;

namespace NetRatel.Shared.Contracts.Tasks;

public sealed record ExecutionLogDto(
    string? RequestId,
    string? Plain,
    string? Html,
    int ExitCode,
    IReadOnlyList<TaskLogDto> Logs
);

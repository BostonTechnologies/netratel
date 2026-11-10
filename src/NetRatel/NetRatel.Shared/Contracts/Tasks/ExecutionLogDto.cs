using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.Tasks;

public sealed record ExecutionLogDto(
    string? RequestId,
    string? Plain,
    string? Html,
    int? ExitCode,
    IReadOnlyList<TaskLogDto> Logs,
    bool Truncated = false
)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? NextCursor { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool HasMore { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<long>? TruncatedLogIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FullOutputUrl { get; init; }
}

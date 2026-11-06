using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.Flows;

public enum FlowTraceProvenance { Simulated = 1, Recorded = 2, Actual = 3 }
public enum FlowTraceDataState { Missing = 1, Null = 2, Empty = 3, Present = 4, Truncated = 5 }
public enum FlowTraceStepStatus { Completed = 1, Skipped = 2, Planned = 3, Unavailable = 4, Failed = 5, Waiting = 6, AwaitingApproval = 7 }

/// <summary>Captured native identity, not a URL or a grant to read execution details.</summary>
public sealed record FlowExecutionReferenceDto(FlowExecutionSourceKind SourceKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefinitionId, string ExecutionId)
{
    public static FlowExecutionReferenceDto? Create(FlowExecutionSourceKind sourceKind, string? definitionId, string? executionId)
    {
        if (executionId is null) return null;
        if (sourceKind == FlowExecutionSourceKind.Script && !PositiveDecimal(definitionId)) definitionId = null;
        var reference = new FlowExecutionReferenceDto(sourceKind, definitionId, executionId);
        return IsValid(reference) ? reference : null;
    }

    public static bool IsValid(FlowExecutionReferenceDto? value) => value is not null && (value.SourceKind switch
    {
        FlowExecutionSourceKind.Job => PositiveDecimal(value.DefinitionId) && PositiveDecimal(value.ExecutionId),
        FlowExecutionSourceKind.Script =>
            (value.DefinitionId is null || PositiveDecimal(value.DefinitionId)) &&
            value.ExecutionId is not ("." or "..") &&
            FlowGraphValidator.IsBoundedText(value.ExecutionId, FlowExecutionEventLimits.MaximumIdentifierLength),
        _ => false
    });

    private static bool PositiveDecimal(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 &&
        id.ToString(CultureInfo.InvariantCulture) == value;
}

/// <summary>Missing is different from a captured null or empty value. A truncated excerpt is text, never partial JSON.</summary>
public sealed record FlowTraceSnapshotDto(FlowTraceDataState State)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(FlowNullableJsonElementConverter))]
    public JsonElement? Value { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OriginalUtf8Bytes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Excerpt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reference { get; init; }
}

public sealed record FlowNodeTraceDto(int Sequence, Guid NodeId, string NodeName, FlowTraceStepStatus Status,
    string Outcome, FlowTraceProvenance Provenance, FlowTraceSnapshotDto Input, FlowTraceSnapshotDto Output)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedPort { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? EdgeId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? NextNodeId { get; init; }
    public int Attempt { get; init; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMilliseconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowExecutionReferenceDto? ExecutionReference { get; init; }
}

/// <summary>Playback presents captured decisions only; this DTO conveys no execution authority.</summary>
public sealed record FlowTraceDto(int GraphSchemaVersion, string ConfigurationHash, FlowTraceProvenance Provenance,
    IReadOnlyList<FlowNodeTraceDto> Steps, bool Truncated = false)
{
    public int SchemaVersion { get; init; } = 1;
}

public static class FlowTraceLimits
{
    public const int MaximumSteps = 64;
    public const int MaximumSnapshotBytes = 4096;
    public const int MaximumSnapshotTotalBytes = 65536;
    public const int MaximumExcerptLength = 256;
    public const int MaximumTraceBytes = 131072;
}

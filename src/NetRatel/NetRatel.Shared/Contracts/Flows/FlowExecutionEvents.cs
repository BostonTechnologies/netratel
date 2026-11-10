using System.Text.Json.Serialization;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Shared.Contracts.Flows;

public enum FlowExecutionSourceKind { Job = 1, Script = 2, Request = 3 }
public enum FlowExecutionOutcome { Succeeded = 1, Failed = 2, TimedOut = 3, Cancelled = 4, Unknown = 5, Unsupported = 6, Denied = 7 }

/// <summary>Identity captured at submission, independent of later definition edits or renames.</summary>
public sealed record FlowExecutionDefinitionDto(string? Id, string? Name = null, string? Revision = null, string? ContentHash = null);
public sealed record FlowExecutionTargetDto(Guid? AgentId, string? DisplayName = null, string? HostName = null);

/// <summary>A bounded reference is not permission to read its contents. Missing historical evidence stays unavailable.</summary>
public sealed record FlowExecutionDataReferenceDto(string Kind, string Id, string? ContentHash = null,
    bool Available = false, bool Truncated = false);

public sealed record FlowExecutionCausationDto(string RootOccurrenceId, Guid? ParentEventId = null,
    Guid? FlowRunId = null, Guid? NodeId = null, int HopCount = 0, int RecoveryAttempt = 0);

/// <summary>
/// Produced by a trusted source writer. InputsReference identifies protected immutable execution material;
/// it never contains credentials or inputs. Replay admission must resolve and verify that material again.
/// </summary>
public sealed record FlowExecutionReplayDescriptorDto(FlowExecutionSourceKind SourceKind,
    FlowExecutionDefinitionDto Definition, FlowExecutionTargetDto Target, string InputsReference,
    string SourceExecutionId, string SourceAttemptId, bool Supported, string? UnsupportedReason = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TargetPlanDigest { get; init; }
}

public sealed record FlowExecutionAggregateDto(int TargetCount, string TargetPlanDigest, JobRunNodeCountsDto Counts)
{
    // Only the canonical legacy fixed-target schedule writer may populate this compatibility identity.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? LegacyTargetAgentId { get; init; }
}

/// <summary>
/// Immutable execution facts, separate from AI findings and flow outputs. Decimal identifiers remain strings
/// across JSON/JavaScript. This payload cannot confer execution authority on its consumer.
/// </summary>
public sealed record FlowExecutionEventDto(Guid EventId, Guid OccurrenceId, int TenantId,
    FlowExecutionSourceKind SourceKind, FlowExecutionDefinitionDto Definition, string ExecutionId,
    string AttemptId, FlowExecutionTargetDto Target, FlowExecutionOutcome Outcome, string RawStatus,
    DateTimeOffset OccurredAtUtc, DateTimeOffset ObservedAtUtc)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowExecutionAggregateDto? Aggregate { get; init; }
    public int SchemaVersion { get; init; } = FlowExecutionEventLimits.SchemaVersion;
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string? ReasonCode { get; init; }
    public string? Reason { get; init; }
    public int? ExitCode { get; init; }
    public string? CorrelationId { get; init; }
    public string? RequestKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowExecutionSourceKind? DefinitionSourceKind { get; init; }
    public string? ParentExecutionId { get; init; }
    public string? RootExecutionId { get; init; }
    public FlowExecutionCausationDto? Causation { get; init; }
    public FlowExecutionReplayDescriptorDto? Replay { get; init; }
    public IReadOnlyList<FlowExecutionDataReferenceDto>? Results { get; init; }
    public string Provenance { get; init; } = "authoritative-transition";
}

/// <summary>Normalization is for accepted terminal state transitions, never stale/disconnected observations.</summary>
public static class FlowExecutionOutcomes
{
    public static FlowExecutionOutcome? FromAuthoritativeStatus(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "SUCCESS" or "SUCCEEDED" or "COMPLETED" => FlowExecutionOutcome.Succeeded,
        "FAILURE" or "FAILED" => FlowExecutionOutcome.Failed,
        "TIMEDOUT" or "TIMED_OUT" or "TIMEOUT" => FlowExecutionOutcome.TimedOut,
        "CANCELLED" or "CANCELED" => FlowExecutionOutcome.Cancelled,
        "UNKNOWN" or "DELIVERYUNKNOWN" or "DELIVERY_UNKNOWN" => FlowExecutionOutcome.Unknown,
        "UNSUPPORTED" => FlowExecutionOutcome.Unsupported,
        "DENIED" or "REJECTED" => FlowExecutionOutcome.Denied,
        _ => null
    };
}

public static class FlowExecutionEventLimits
{
    public const int SchemaVersion = 1;
    public const string OutboxType = "Flow.ExecutionLifecycle.V1";
    public const int MaximumEnvelopeBytes = 16384;
    public const int MaximumReasonLength = 2048;
    public const int MaximumReferences = 16;
    public const int MaximumIdentifierLength = 256;
    public const int MaximumHopCount = 8;
    public const int DefaultRecoveryAttempts = 1;
    public const int MaximumRecoveryAttempts = 3;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FlowExecutionEventDto))]
public partial class FlowExecutionEventJsonContext : JsonSerializerContext { }

using NetRatel.Shared.Contracts.Terminals;

namespace NetRatel.Application.Terminals;

public readonly record struct TerminalShadowSessionKey(
    int TenantId,
    string ClientId,
    string TerminalSessionId,
    TerminalTransportKind Transport)
{
    public bool IsValid =>
        TenantId > 0 &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(TerminalSessionId) &&
        Transport is TerminalTransportKind.ApiWebSocket or TerminalTransportKind.AkkaGateway;

    public string EntityId => $"{TenantId}:{ClientId}:{TerminalSessionId}:{Transport}";

    public override string ToString() => EntityId;
}

public enum TerminalShadowEventKind
{
    SessionRequested = 0,
    SessionOpened = 1,
    InputObserved = 2,
    OutputObserved = 3,
    ResizeObserved = 4,
    SessionCloseRequested = 5,
    SessionClosed = 6,
    SessionFailed = 7
}

public enum TerminalShadowStreamType
{
    Lifecycle = 0,
    StandardInput = 1,
    StandardOutput = 2,
    StandardError = 3,
    Resize = 4
}

public enum TerminalShadowSequenceStream
{
    Input = 0,
    Output = 1,
    Resize = 2
}

public enum TerminalShadowCloseKind
{
    None = 0,
    OperatorRequested = 1,
    RemoteClosed = 2,
    AgentError = 3,
    TransportLost = 4
}

public enum TerminalShadowSessionStatus
{
    Requested = 0,
    Opened = 1,
    Closing = 2,
    Closed = 3,
    Failed = 4
}

public enum TerminalShadowMessageDisposition
{
    Accepted = 0,
    Duplicate = 1,
    StaleSequence = 2,
    SequenceConflict = 3,
    InvalidTransition = 4,
    InvalidObservation = 5,
    IdentityMismatch = 6
}

/// <summary>
/// Fixed-size terminal metadata. This contract deliberately cannot carry PTY
/// input, output, payload JSON, working directories, or raw close reasons.
/// </summary>
public sealed record TerminalShadowEvent(
    TerminalShadowSessionKey Session,
    TerminalShadowEventKind Kind,
    TerminalShadowStreamType StreamType,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    int PayloadLength = 0,
    int? Columns = null,
    int? Rows = null,
    TerminalShadowCloseKind CloseKind = TerminalShadowCloseKind.None,
    string Source = "api-terminal-observer",
    bool IsAuthoritative = false);

public sealed record RecordTerminalShadowEvent(TerminalShadowEvent Event);

public sealed record GetTerminalShadowState(TerminalShadowSessionKey Session);

public sealed record ProbeTerminalShadowRoute;

public sealed record TerminalShadowRetentionPolicy(
    int MaxRetainedMetadata,
    int MaxRetainedByteCount,
    int MaxObservedFrameBytes)
{
    public static TerminalShadowRetentionPolicy Default { get; } = new(
        MaxRetainedMetadata: 32,
        MaxRetainedByteCount: 64 * 1024,
        MaxObservedFrameBytes: 16 * 1024);

    public bool IsValid =>
        MaxRetainedMetadata > 0 &&
        MaxRetainedByteCount > 0 &&
        MaxObservedFrameBytes > 0 &&
        MaxObservedFrameBytes <= MaxRetainedByteCount;
}

public sealed record TerminalShadowMessageResult(
    TerminalShadowSessionKey Session,
    TerminalShadowMessageDisposition Disposition,
    TerminalShadowSessionStatus? CurrentStatus,
    ulong MaximumObservedSequence,
    int RetainedMetadataCount,
    int RetainedByteCount,
    ulong DroppedMetadataEvents,
    ulong CoalescedMetadataEvents);

public sealed record TerminalShadowMetadataEntry(
    TerminalShadowEventKind Kind,
    TerminalShadowStreamType StreamType,
    DateTimeOffset Timestamp,
    ulong? Sequence,
    int PayloadLength,
    int? Columns,
    int? Rows,
    TerminalShadowCloseKind CloseKind);

public sealed record TerminalShadowStreamSequence(
    TerminalShadowSequenceStream Stream,
    ulong LastAcceptedSequence);

public sealed record TerminalShadowLifecycleCounts(
    ulong Requested,
    ulong Opened,
    ulong CloseRequested,
    ulong Closed,
    ulong Failed);

public sealed record TerminalShadowState(
    TerminalShadowSessionKey Session,
    TerminalShadowSessionStatus? Status,
    IReadOnlyList<TerminalShadowStreamSequence> Sequences,
    IReadOnlyList<TerminalShadowMetadataEntry> RecentMetadata,
    int RetainedByteCount,
    ulong DroppedMetadataEvents,
    ulong CoalescedMetadataEvents,
    ulong OversizeFrames,
    ulong RejectedStaleEvents,
    ulong SequenceConflicts,
    ulong InvalidTransitions,
    ulong InvalidObservations,
    ulong InputFrames,
    ulong OutputFrames,
    ulong ResizeEvents,
    ulong InputBytes,
    ulong OutputBytes,
    ulong MaximumObservedSequence,
    TerminalShadowLifecycleCounts LifecycleCounts,
    string Source,
    bool IsAuthoritative);

public sealed record TerminalShadowRouteStatus(
    int ActiveTerminalSessions,
    ulong ClosedTerminalSessions,
    ulong FailedTerminalSessions,
    int SessionActorCount,
    ulong AcceptedEvents,
    ulong DuplicateEvents,
    ulong RejectedStaleEvents,
    ulong SequenceConflicts,
    ulong InvalidTransitions,
    ulong InvalidObservations,
    ulong IdentityConflicts,
    ulong DroppedMetadataEvents,
    ulong CoalescedMetadataEvents,
    int RetainedMetadataCount,
    ulong InputFrames,
    ulong OutputFrames,
    ulong ResizeEvents,
    ulong InputBytes,
    ulong OutputBytes,
    ulong MaximumObservedSequence,
    TerminalShadowLifecycleCounts LifecycleCounts,
    DateTimeOffset StartedAtUtc,
    string Mode,
    string Authority);

public interface ITerminalShadowRouter
{
    Task<TerminalShadowMessageResult> RecordAsync(
        RecordTerminalShadowEvent message,
        CancellationToken cancellationToken);

    Task<TerminalShadowState> GetStateAsync(
        TerminalShadowSessionKey session,
        CancellationToken cancellationToken);

    Task<TerminalShadowRouteStatus> ProbeAsync(CancellationToken cancellationToken);
}

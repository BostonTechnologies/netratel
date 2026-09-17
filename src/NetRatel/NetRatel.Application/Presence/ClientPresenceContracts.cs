namespace NetRatel.Application.Presence;

/// <summary>
/// Authenticated Phase 1 client identity. The agent identifier comes from the
/// validated JWT and is intentionally not inferred from a SpacetimeDB identity.
/// </summary>
public readonly record struct ClientKey(int TenantId, Guid AgentId)
{
    public bool IsValid => TenantId > 0 && AgentId != Guid.Empty;

    public string EntityId => $"{TenantId}:{AgentId:N}";

    public override string ToString() => EntityId;
}

public enum ShadowPresenceStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2
}

public enum PresenceMessageDisposition
{
    Accepted = 0,
    Duplicate = 1,
    StaleConnectionEpoch = 2,
    StaleSequence = 3,
    ConnectionMismatch = 4,
    NoActiveSession = 5
}

public interface IClientPresenceMessage
{
    ClientKey Client { get; }
}

public sealed record StartGatewayPresenceSession(
    ClientKey Client,
    Guid ConnectionId,
    Guid OperationId,
    string ProtocolVersion,
    string AgentVersion,
    IReadOnlyList<string> Capabilities,
    string? LegacySpacetimeIdentity,
    DateTimeOffset ReceivedAtUtc) : IClientPresenceMessage;

public sealed record GatewayPresenceSessionStarted(
    ClientKey Client,
    Guid ConnectionId,
    long ConnectionEpoch,
    PresenceMessageDisposition Disposition,
    DateTimeOffset ReceivedAtUtc);

public sealed record RecordGatewayHeartbeat(
    ClientKey Client,
    Guid ConnectionId,
    long ConnectionEpoch,
    Guid OperationId,
    ulong Sequence,
    DateTimeOffset ReceivedAtUtc) : IClientPresenceMessage;

public sealed record EndGatewayPresenceSession(
    ClientKey Client,
    Guid ConnectionId,
    long ConnectionEpoch,
    string Reason,
    DateTimeOffset ReceivedAtUtc) : IClientPresenceMessage;

public sealed record PresenceMessageResult(
    ClientKey Client,
    long? ConnectionEpoch,
    PresenceMessageDisposition Disposition,
    ulong LastAcceptedSequence);

public sealed record GetClientPresence(ClientKey Client) : IClientPresenceMessage;

public sealed record ClientPresenceSnapshot(
    ClientKey Client,
    ShadowPresenceStatus Status,
    long? ConnectionEpoch,
    Guid? ConnectionId,
    ulong LastAcceptedSequence,
    DateTimeOffset? LastReceivedAtUtc,
    string? AgentVersion,
    IReadOnlyList<string> Capabilities,
    string? LegacySpacetimeIdentity,
    string Source,
    bool IsAuthoritative);

public sealed record ProbeClientPresenceRoute;

public sealed record ClientPresenceRouteStatus(
    int ActiveClientActors,
    DateTimeOffset StartedAtUtc,
    string Mode);

/// <summary>
/// Immutable update sent by a per-client presence actor to the process-local
/// Akka read model. This is deliberately separate from the legacy client
/// directory and never contains a SpacetimeDB identity.
/// </summary>
public sealed record TrackClientPresenceSnapshot(ClientPresenceSnapshot Snapshot);

public sealed record GetClientPresenceReadModel;

public sealed record ClientPresenceReadModelSnapshot(
    long Revision,
    IReadOnlyList<ClientPresenceSnapshot> Items);

/// <summary>
/// Process-local presence transition. It is diagnostic unless the explicit
/// DEV-only gateway presence authority mode marks it authoritative.
/// </summary>
public sealed record ShadowPresenceChanged(
    ClientKey Client,
    ShadowPresenceStatus Status,
    long ConnectionEpoch,
    DateTimeOffset ChangedAtUtc,
    string Reason,
    bool IsAuthoritative = false);

/// <summary>
/// Transport-neutral entry point used by the gRPC gateway and health checks.
/// Implementations do not update existing SpacetimeDB or PostgreSQL presence
/// records. Authority is selected explicitly by migration configuration.
/// </summary>
public interface IClientPresenceRouter
{
    Task<GatewayPresenceSessionStarted> StartSessionAsync(
        StartGatewayPresenceSession message,
        CancellationToken cancellationToken);

    Task<PresenceMessageResult> RecordHeartbeatAsync(
        RecordGatewayHeartbeat message,
        CancellationToken cancellationToken);

    Task<PresenceMessageResult> EndSessionAsync(
        EndGatewayPresenceSession message,
        CancellationToken cancellationToken);

    Task<ClientPresenceSnapshot> GetSnapshotAsync(
        ClientKey client,
        CancellationToken cancellationToken);

    Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Read-only view of the active gateway presence projection. The DEV canary
/// implementation is process-local and is rebuilt as gateway clients reconnect.
/// </summary>
public interface IClientPresenceReadModel
{
    Task<ClientPresenceReadModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

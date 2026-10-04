namespace NetRatel.Application.Presence;

/// <summary>
/// Authenticated client identity. The agent identifier comes from the
/// validated JWT and is intentionally not inferred from a legacy identity.
/// </summary>
public readonly record struct ClientKey(int TenantId, Guid AgentId)
{
    public bool IsValid => TenantId > 0 && AgentId != Guid.Empty;

    public string EntityId => $"{TenantId}:{AgentId:N}";

    public override string ToString() => EntityId;
}

public enum ClientPresenceStatus
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
    NoActiveSession = 5,
    AuthenticationExpired = 6,
    InvalidAuthenticationRenewal = 7,
    AdmissionExpired = 8,
    AdmissionCancelled = 9,
    AdmissionCapacityExceeded = 10
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
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? AuthenticationExpiresAtUtc = null,
    DateTimeOffset? AdmissionExpiresAtUtc = null,
    bool ProvisionalAdmission = false) : IClientPresenceMessage;

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
    DateTimeOffset ReceivedAtUtc,
    double? HeartbeatRoundTripMilliseconds = null,
    DateTimeOffset? LatencyMeasuredAtUtc = null,
    DateTimeOffset? RenewedAuthenticationExpiresAtUtc = null) : IClientPresenceMessage;

public sealed record EndGatewayPresenceSession(
    ClientKey Client,
    Guid ConnectionId,
    long ConnectionEpoch,
    string Reason,
    DateTimeOffset ReceivedAtUtc,
    bool CancelPendingAdmission = false) : IClientPresenceMessage;

public sealed record PresenceMessageResult(
    ClientKey Client,
    long? ConnectionEpoch,
    PresenceMessageDisposition Disposition,
    ulong LastAcceptedSequence);

public sealed record GetClientPresence(ClientKey Client) : IClientPresenceMessage;

public sealed record ClientPresenceSnapshot(
    ClientKey Client,
    ClientPresenceStatus Status,
    long? ConnectionEpoch,
    Guid? ConnectionId,
    ulong LastAcceptedSequence,
    DateTimeOffset? LastReceivedAtUtc,
    string? AgentVersion,
    IReadOnlyList<string> Capabilities,
    string? LegacySpacetimeIdentity,
    string Source,
    bool IsAuthoritative,
    double? LatencyMilliseconds = null,
    DateTimeOffset? LatencyMeasuredAtUtc = null,
    DateTimeOffset? LatencyExpiresAtUtc = null,
    DateTimeOffset? AuthenticationExpiresAtUtc = null);

public sealed record ProbeClientPresenceRoute;

public sealed record ClientPresenceRouteStatus(
    int ActiveClientActors,
    DateTimeOffset StartedAtUtc,
    string Mode);

/// <summary>
/// Immutable update sent by a per-client presence actor to the process-local
/// Akka read model. It never contains a legacy identity.
/// </summary>
public sealed record TrackClientPresenceSnapshot(ClientPresenceSnapshot Snapshot);

public sealed record GetClientPresenceReadModel;

public sealed record ClientPresenceReadModelSnapshot(
    long Revision,
    IReadOnlyList<ClientPresenceSnapshot> Items);

/// <summary>
/// Process-local presence transition emitted by the authenticated gateway.
/// </summary>
public sealed record ClientPresenceChanged(
    ClientKey Client,
    ClientPresenceStatus Status,
    long ConnectionEpoch,
    DateTimeOffset ChangedAtUtc,
    string Reason,
    bool IsAuthoritative = false);

/// <summary>
/// Transport-neutral entry point used by the gRPC gateway and health checks.
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
/// Read-only view of the active gateway presence projection. This process-local
/// projection is rebuilt as gateway clients reconnect.
/// </summary>
public interface IClientPresenceReadModel
{
    Task<ClientPresenceReadModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

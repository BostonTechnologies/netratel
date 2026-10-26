namespace NetRatel.Shared.Contracts.Services;

public enum ClientServicePlatform
{
    Unknown = 0,
    Windows = 1,
    LinuxSystemd = 2
}

public enum ClientServiceState
{
    Unknown = 0,
    Running = 1,
    Stopped = 2,
    Failed = 3,
    Starting = 4,
    Stopping = 5,
    Paused = 6,
    Missing = 7,
    Unsupported = 8
}

public enum ServiceCollectionStatus
{
    Unknown = 0,
    Complete = 1,
    Partial = 2,
    Error = 3,
    Unsupported = 4
}

public enum ServiceSnapshotKind
{
    Unknown = 0,
    Inventory = 1,
    Watch = 2
}

/// <summary>A stable SCM name or exact systemd unit, with no executable or environment data.</summary>
public sealed record ClientServiceObservation(
    string Name,
    string DisplayName,
    ClientServicePlatform Platform,
    ClientServiceState State,
    string RawState,
    string? StartMode,
    string? LoadState,
    string? ActiveState,
    string? SubState,
    string? UnitFileState,
    DateTimeOffset ObservedAtUtc,
    bool AuthoritativeMissing = false);

/// <summary>One bounded collection. Only complete inventory replaces the offline cache.</summary>
public sealed record ServiceCollectionResult(
    Guid CollectionId,
    ServiceSnapshotKind Kind,
    ServiceCollectionStatus Status,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<ClientServiceObservation> Services,
    string? ErrorCode = null,
    ulong WatchPolicyRevision = 0,
    Guid? RefreshRequestId = null);

public sealed record ClientServicesSnapshotDto(
    Guid CollectionId,
    long ConnectionEpoch,
    ulong Sequence,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyList<ClientServiceObservation> Services,
    ServiceCollectionStatus Status = ServiceCollectionStatus.Complete,
    string? ErrorCode = null);

public sealed record ServiceCollectionAttemptDto(
    Guid CollectionId,
    ServiceSnapshotKind Kind,
    ServiceCollectionStatus Status,
    long ConnectionEpoch,
    ulong Sequence,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    ulong WatchPolicyRevision,
    string? ErrorCode = null,
    bool IsFinal = false,
    Guid? RefreshRequestId = null,
    Guid? RegistrationId = null);

/// <summary>Server-selected exact names. Expiration stops stale watches; it never runs service control.</summary>
public sealed record ClientServiceWatchPolicyDto(
    ulong Revision,
    IReadOnlyList<string> ServiceNames,
    uint WatchIntervalSeconds,
    uint InventoryIntervalSeconds,
    DateTimeOffset ExpiresAtUtc,
    Guid? RefreshRequestId = null);

/// <summary>Cached inventory and current watch evidence remain distinct from the latest collection attempt.</summary>
public sealed record ClientServicesReadModelDto(
    int TenantId,
    Guid AgentId,
    ClientServicesSnapshotDto? LastCompleteInventory,
    ServiceCollectionAttemptDto? LatestAttempt,
    IReadOnlyList<ClientServiceObservation> WatchedServices,
    IReadOnlyList<string> MonitoredServiceNames,
    ulong WatchPolicyRevision,
    bool Connected,
    bool SupportsServices,
    DateTimeOffset GeneratedAtUtc,
    long Revision = 0,
    ClientServicesSnapshotDto? LastPartialInventory = null,
    ServiceCollectionAttemptDto? LatestInventoryAttempt = null,
    bool SupportsServiceControl = false,
    bool CanControlServices = false,
    bool SupportsRefreshCorrelation = false,
    IReadOnlyList<ClientServicesRefreshRequestDto>? RefreshRequests = null);

public enum ClientServicesRefreshStatus
{
    Requested = 0,
    Offline = 1,
    Unsupported = 2,
    Throttled = 3
}

public sealed record ClientServicesRefreshResponse(
    ClientServicesRefreshStatus Status,
    Guid? RequestId,
    DateTimeOffset? RetryAfterUtc = null,
    bool CorrelationSupported = false);

public enum ClientServicesRefreshOutcome
{
    Pending = 0,
    Complete = 1,
    Partial = 2,
    Error = 3,
    Unsupported = 4,
    Offline = 5,
    TimedOut = 6,
    Unattributed = 7
}

/// <summary>Server admission and terminal evidence for one request on one telemetry registration.</summary>
public sealed record ClientServicesRefreshRequestDto(
    Guid RequestId,
    Guid ConnectionId,
    long ConnectionEpoch,
    Guid RegistrationId,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset DeadlineUtc,
    bool CorrelationSupported,
    ClientServicesRefreshOutcome Outcome = ClientServicesRefreshOutcome.Pending,
    Guid? CollectionId = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? ErrorCode = null);

/// <summary>Generic collection/transport bounds; the existing 16 KiB gRPC limit remains unchanged.</summary>
public static class ClientServicesLimits
{
    public const string Capability = "client-services-v1";
    public const string RefreshCorrelationCapability = "client-services-refresh-v1";
    public const int MaximumRefreshHistory = 128;
    public const int MaximumChunkPayloadBytes = 12 * 1024 - 1;
    public const int MaximumInventoryBytes = 1024 * 1024;
    public const int MaximumChunks = 128;
    public const int MaximumServices = 2048;
    public const int MaximumWatchServices = 64;
    public const int MaximumNameLength = 256;
    public const int MaximumDisplayNameLength = 512;
    public const int MaximumRawStateLength = 128;
    public const int MaximumErrorCodeLength = 64;
    public const uint DefaultWatchIntervalSeconds = 30;
    public const uint DefaultInventoryIntervalSeconds = 900;
    public const uint MinimumWatchIntervalSeconds = 15;
    public const uint MaximumWatchIntervalSeconds = 300;
    public const uint MinimumInventoryIntervalSeconds = 300;
    public const uint MaximumInventoryIntervalSeconds = 3600;
    public static readonly TimeSpan CollectionTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AssemblyTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RefreshTimeout = MinimumRefreshInterval + CollectionTimeout + AssemblyTimeout + TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaximumPolicyLifetime = TimeSpan.FromHours(1);
    // Server-issued policies must leave room for client clock skew and transport before this maximum is checked.
    public static readonly TimeSpan PolicyClockSkewHeadroom = TimeSpan.FromMinutes(1);
}

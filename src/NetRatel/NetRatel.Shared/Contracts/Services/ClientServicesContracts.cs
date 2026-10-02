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
    ulong WatchPolicyRevision = 0);

public sealed record ClientServicesSnapshotDto(
    Guid CollectionId,
    long ConnectionEpoch,
    ulong Sequence,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyList<ClientServiceObservation> Services);

public sealed record ServiceCollectionAttemptDto(
    Guid CollectionId,
    ServiceSnapshotKind Kind,
    ServiceCollectionStatus Status,
    long ConnectionEpoch,
    ulong Sequence,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    ulong WatchPolicyRevision,
    string? ErrorCode = null);

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
    long Revision = 0);

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
    DateTimeOffset? RetryAfterUtc = null);

/// <summary>Generic collection/transport bounds; the existing 16 KiB gRPC limit remains unchanged.</summary>
public static class ClientServicesLimits
{
    public const string Capability = "client-services-v1";
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
    public static readonly TimeSpan MaximumPolicyLifetime = TimeSpan.FromHours(1);
}

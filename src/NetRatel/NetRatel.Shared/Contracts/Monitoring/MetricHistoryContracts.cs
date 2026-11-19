using System.Text.Json;
using System.Text.Json.Serialization;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Shared.Contracts.Monitoring;

[JsonConverter(typeof(JsonStringEnumConverter<MetricHistoryKind>))]
public enum MetricHistoryKind
{
    CpuPercent = 1, MemoryPercent, MemoryUsedBytes, MemoryTotalBytes, DiskUsedPercent, NetworkRx, NetworkTx, ServiceState
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<MetricHistoryQuality>))]
public enum MetricHistoryQuality
{
    None = 0, Legacy = 1, LegacyWindowsCpu = 2, WarmUp = 4, Reset = 8, Missing = 16, Failed = 32,
    Unsupported = 64, InventoryPartial = 128, EstimatedTiming = 256, PartialEdge = 512,
    PartialCoverage = 1024, UnclassifiedDisk = 2048
}

public static class MetricHistoryLimits
{
    public const int BaseSeconds = 60;
    public const int BaseDays = 14;
    public const int FiveMinuteDays = 90;
    public const int HourlyDays = 365;
    public const int MaximumBaseDays = 90;
    public const int MaximumFiveMinuteDays = 365;
    public const int MaximumHourlyDays = 730;
    public const int MaximumSeriesPerQuery = 12;
    public const int MaximumCataloguePage = 200;
    public const int DefaultPointsPerSeries = 1000;
    public const int MaximumPointsPerSeries = 2000;
    public const int MaximumTotalPoints = 8000;
    public const int MaximumServiceDetails = 2000;
    public const int MaximumWatches = 256;
    public const int MaximumWatchesPerAgent = 32;

    public static string Unit(MetricHistoryKind kind) => kind switch
    {
        MetricHistoryKind.CpuPercent or MetricHistoryKind.MemoryPercent or MetricHistoryKind.DiskUsedPercent => "%",
        MetricHistoryKind.MemoryUsedBytes or MetricHistoryKind.MemoryTotalBytes => "B",
        MetricHistoryKind.NetworkRx or MetricHistoryKind.NetworkTx => "B/s",
        MetricHistoryKind.ServiceState => "state",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public sealed record MetricServiceWatch(Guid AgentId, ClientServicePlatform Platform, string ServiceName);
public sealed record MetricHistoryPolicy(long Revision, bool Enabled, int BaseDays, int FiveMinuteDays, int HourlyDays,
    MetricServiceWatch[] Watches, DateTimeOffset? UpdatedAtUtc = null)
{
    public static MetricHistoryPolicy Default => new(0, true, MetricHistoryLimits.BaseDays, MetricHistoryLimits.FiveMinuteDays,
        MetricHistoryLimits.HourlyDays, []);
}

public sealed record MetricHistoryPolicyWrite(long ExpectedRevision, bool Enabled, int BaseDays, int FiveMinuteDays, int HourlyDays,
    MetricServiceWatch[] Watches, string Reason);

public sealed record MetricWatchActivation(MetricServiceWatch Watch, bool Selected, bool RequiredByAlerts,
    bool ActiveInPolicy, bool AwaitingEvidence);

public sealed record MetricCollectionStatus(
    bool Enabled, DateTimeOffset? LastAcceptedAtUtc, DateTimeOffset? LastPersistedAtUtc,
    DateTimeOffset? LastRollupAtUtc, DateTimeOffset? LastPurgeAtUtc, DateTimeOffset? OldestUnrolledAtUtc,
    long RetainedRows, long TenantRowLimit, long InstanceRowLimit,
    int QueueFrames, long QueueBytes, double? QueueAgeSeconds, long DroppedFrames, long LateObservations,
    long PartialFrames, string? Disposition, DateTimeOffset? SealedBeforeUtc);

public sealed record MetricHistorySettings(MetricHistoryPolicy Policy, MetricCollectionStatus Status,
    IReadOnlyList<MetricWatchActivation> Activations)
{
    public int BaseResolutionSeconds => MetricHistoryLimits.BaseSeconds;
    public int MaximumBaseDays => MetricHistoryLimits.MaximumBaseDays;
    public int MaximumFiveMinuteDays => MetricHistoryLimits.MaximumFiveMinuteDays;
    public int MaximumHourlyDays => MetricHistoryLimits.MaximumHourlyDays;
    public int MaximumWatches => MetricHistoryLimits.MaximumWatches;
    public int MaximumWatchesPerAgent => MetricHistoryLimits.MaximumWatchesPerAgent;
}

public sealed record MetricSeriesDescription(long Id, Guid AgentId, MetricHistoryKind Kind, string ResourceKey,
    string DisplayName, string Unit, DateTimeOffset FirstRetainedAtUtc, DateTimeOffset LastObservedAtUtc,
    MetricHistoryQuality Quality, bool RecentlyObserved);

public sealed record MetricCatalogue(IReadOnlyList<MetricSeriesDescription> Series, string? NextCursor,
    MetricCollectionStatus Status, bool? ClientOnline = null);

public sealed record MetricHistoryRange(long[] SeriesIds, DateTimeOffset FromUtc, DateTimeOffset ToExclusiveUtc,
    int? ResolutionSeconds = null, int? MaximumPointsPerSeries = null)
{
    public MetricHistoryQuery ForAgent(Guid agentId) => new(agentId, SeriesIds, FromUtc, ToExclusiveUtc, ResolutionSeconds, MaximumPointsPerSeries);
}

public sealed record MetricHistoryQuery(Guid AgentId, long[] SeriesIds, DateTimeOffset FromUtc, DateTimeOffset ToExclusiveUtc,
    int? ResolutionSeconds = null, int? MaximumPointsPerSeries = null);

public sealed record MetricServiceDuration(long RunningMicroseconds, long StoppedMicroseconds, long OtherKnownMicroseconds,
    long UnknownMicroseconds);
public sealed record MetricServiceCheckpoint(DateTimeOffset ObservedAtUtc, ClientServiceState State, bool Predecessor);

public sealed record MetricHistoryStatistics(long Count, double? Last, DateTimeOffset? LastObservedAtUtc,
    double? Minimum, double? Maximum, double? WeightedAverage, long CoveredMicroseconds, string? TransferredBytes,
    string? DiskTotalBytes, string? DiskFreeBytes, MetricHistoryQuality Quality, MetricServiceDuration? ServiceDuration);
public sealed record MetricHistoryPoint(DateTimeOffset FromUtc, DateTimeOffset ToExclusiveUtc, MetricHistoryStatistics Statistics);
public sealed record MetricHistorySeries(MetricSeriesDescription Series, MetricHistoryStatistics Summary,
    IReadOnlyList<MetricHistoryPoint> Points, IReadOnlyList<MetricServiceCheckpoint> ServiceDetail, bool ServiceDetailLimited);
public sealed record MetricHistoryResponse(DateTimeOffset FromUtc, DateTimeOffset ToExclusiveUtc, int ResolutionSeconds,
    DateTimeOffset EarliestPolicyUtc, DateTimeOffset ServiceDetailFromUtc, string Disposition,
    MetricCollectionStatus Status, IReadOnlyList<MetricHistorySeries> Series);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(MetricHistoryPolicy))]
[JsonSerializable(typeof(MetricHistoryPolicyWrite))]
[JsonSerializable(typeof(MetricHistorySettings))]
[JsonSerializable(typeof(MetricCatalogue))]
[JsonSerializable(typeof(MetricHistoryRange))]
[JsonSerializable(typeof(MetricHistoryQuery))]
[JsonSerializable(typeof(MetricHistoryResponse))]
public sealed partial class MetricHistoryJsonContext : JsonSerializerContext;

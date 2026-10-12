using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.Monitoring;

/// <summary>Bounded immutable message data. Complete membership is retained only on the current monitoring series.</summary>
public sealed record MonitoringOfflineSnapshotDto(int OfflineCount, int MonitoredCount, string ClientNames,
    bool Truncated, int SuppressedCount = 0)
{
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var count = OfflineCount - SuppressedCount;
            if (count == 0) return OfflineCount == 0 ? "All monitored clients are online." : $"{OfflineCount} offline clients are bypassed.";
            var status = count == 1 ? "1 client is offline" : $"{count} clients are offline";
            return Truncated || string.IsNullOrWhiteSpace(ClientNames) ? status + "." : status + ": " + ClientNames + ".";
        }
    }
}

public static class MonitoringOfflinePresentation
{
    public const string ResourceKey = "clients:offline";
    public const int DefaultGraceSeconds = 60;
    public const int MaximumListedClients = 10;
    public const int MaximumClientNamesLength = 512;
    public static readonly TimeSpan SnapshotFreshness = TimeSpan.FromMinutes(1);

    public static bool IsSeries(MonitoringSeriesKey series) => series.AgentId == Guid.Empty && series.ResourceKey == ResourceKey;
    public static MonitoringSeriesKey Series(int tenantId, Guid ruleId) => new(tenantId, ruleId, Guid.Empty, ResourceKey);

    public static bool IsValid(MonitoringOfflineSnapshotDto? value) => value is not null &&
        value.MonitoredCount is >= 0 and <= MonitoringLimits.MaximumTargetClients &&
        value.OfflineCount >= 0 && value.OfflineCount <= value.MonitoredCount &&
        value.SuppressedCount >= 0 && value.SuppressedCount <= value.OfflineCount &&
        value.ClientNames is { Length: <= MaximumClientNamesLength } && !value.ClientNames.Any(char.IsControl) &&
        (value.Truncated ? value.ClientNames.Length == 0 : value.OfflineCount - value.SuppressedCount <= MaximumListedClients);
}

using NetRatel.Application.Telemetry;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Application.Monitoring;

/// <summary>Converts accepted source projections into fenced evidence without promoting cached or rounded data.</summary>
public static class MonitoringObservationFactory
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;
    private const double LegacyGiBResolution = 0.1d;

    public static MonitoringObservationDto FromTelemetry(MonitoringRuleDto rule, MonitoringSeriesKey series, MonitoringTelemetryInput input)
    {
        var snapshot = input.Snapshot;
        var valid = snapshot.IsAuthoritative && snapshot.Client == input.Fence.Client &&
            snapshot.ConnectionEpoch == input.Fence.ConnectionEpoch && series.TenantId == input.Fence.Client.TenantId &&
            series.AgentId == input.Fence.Client.AgentId && series.RuleId == rule.RuleId && rule.TenantId == series.TenantId;
        double? value = null;
        double resolution = 0;
        if (valid && rule.Condition.Kind == MonitoringMetricKind.CpuUsagePercent && series.ResourceKey == "cpu")
        {
            value = snapshot.Cpu?.UsagePercent;
            resolution = 0.1d;
            valid = value is not null;
        }
        else if (valid && rule.Condition.Kind is MonitoringMetricKind.DiskFreeSpace or MonitoringMetricKind.DiskFreePercent)
        {
            var disks = snapshot.Disks.Where(disk => TryDiskKey(disk.Scope) == series.ResourceKey).ToArray();
            valid = disks.Length == 1 && TryDiskValue(disks[0], rule.Condition.Kind, out value, out resolution);
        }
        else valid = false;
        return new(series, new(snapshot.ConnectionEpoch, snapshot.Sequence), input.Fence.EvidenceStreamId,
            snapshot.ObservedAtUtc, snapshot.ReceivedAtUtc, valid, valid, value, resolution);
    }

    public static MonitoringObservationDto FromServices(MonitoringRuleDto rule, MonitoringSeriesKey series, MonitoringServicesInput input)
    {
        var services = input.Services;
        var attempt = services.LatestAttempt;
        var platform = rule.Condition.ServicePlatform;
        var valid = rule.Condition.Kind == MonitoringMetricKind.ServiceExpectedState && platform is not null &&
            services.Client == input.Fence.Client && services.ConnectionEpoch == input.Fence.ConnectionEpoch &&
            services.ConnectionId == input.Fence.ConnectionId && series.TenantId == input.Fence.Client.TenantId &&
            series.AgentId == input.Fence.Client.AgentId && series.RuleId == rule.RuleId && rule.TenantId == series.TenantId &&
            attempt is { Kind: ServiceSnapshotKind.Watch, Status: ServiceCollectionStatus.Complete } &&
            attempt.ConnectionEpoch == services.ConnectionEpoch && attempt.Sequence == services.LastAcceptedSequence &&
            attempt.WatchPolicyRevision == services.WatchPolicyRevision && services.WatchPolicyRevision > 0;
        ClientServiceObservation? observation = null;
        if (valid)
        {
            var comparer = ClientServiceContractValidator.GetServiceNameComparer(platform!.Value);
            valid = services.MonitoredServiceNames.Any(name => comparer.Equals(name, rule.Condition.ResourceName));
            var matches = services.WatchedServices.Where(service => service.Platform == platform &&
                comparer.Equals(service.Name, rule.Condition.ResourceName)).ToArray();
            valid &= matches.Length == 1;
            if (matches.Length == 1) observation = matches[0];
            valid &= observation is not null && observation.ObservedAtUtc == attempt!.ObservedAtUtc &&
                ClientServiceContractValidator.TryValidateObservation(observation, out _);
        }
        return new(series, new(services.ConnectionEpoch, services.LastAcceptedSequence), input.Fence.EvidenceStreamId,
            observation?.ObservedAtUtc ?? attempt?.ObservedAtUtc ?? default, attempt?.ReceivedAtUtc ?? default,
            valid, valid, ServiceState: observation?.State, AuthoritativeMissing: valid && observation!.AuthoritativeMissing,
            ServiceWatchPolicyRevision: attempt?.WatchPolicyRevision, CurrentServiceWatchPolicyRevision: services.WatchPolicyRevision);
    }

    private static bool TryDiskValue(TelemetryDisk disk, MonitoringMetricKind kind, out double? value, out double resolution)
    {
        value = null;
        resolution = 0;
        if (disk.TotalBytes is not null || disk.FreeBytes is not null)
        {
            if (disk.TotalBytes is not ulong total || disk.FreeBytes is not ulong free || total == 0 || free > total ||
                total > (1UL << 60)) return false;
            var freeValue = (double)free;
            var totalValue = (double)total;
            var freeSpacing = free > (1UL << 53) ? Math.BitIncrement(freeValue) - freeValue : 0d;
            var totalSpacing = total > (1UL << 53) ? Math.BitIncrement(totalValue) - totalValue : 0d;
            if (kind == MonitoringMetricKind.DiskFreeSpace)
            {
                value = freeValue;
                resolution = freeSpacing;
            }
            else
            {
                var percent = freeValue / totalValue * 100d;
                value = percent;
                // Preserve integer conversion and division/multiplication uncertainty near strict thresholds.
                resolution = freeSpacing / totalValue * 100d + percent * totalSpacing / totalValue +
                    4d * (Math.BitIncrement(percent) - percent);
            }
            return true;
        }
        if (!double.IsFinite(disk.TotalGb) || !double.IsFinite(disk.FreeGb) || disk.TotalGb <= 0 || disk.FreeGb < 0 ||
            disk.FreeGb > disk.TotalGb || disk.TotalGb * BytesPerGiB > Math.Pow(1024, 6)) return false;
        if (kind == MonitoringMetricKind.DiskFreeSpace)
        {
            value = disk.FreeGb * BytesPerGiB;
            resolution = LegacyGiBResolution * BytesPerGiB;
        }
        else
        {
            var percent = disk.FreeGb / disk.TotalGb * 100d;
            var lower = Math.Max(0, disk.FreeGb - LegacyGiBResolution / 2) / (disk.TotalGb + LegacyGiBResolution / 2) * 100d;
            var upper = Math.Min(100d, (disk.FreeGb + LegacyGiBResolution / 2) / Math.Max(double.Epsilon, disk.TotalGb - LegacyGiBResolution / 2) * 100d);
            value = percent;
            resolution = 2d * Math.Max(percent - lower, upper - percent);
        }
        return true;
    }

    private static string? TryDiskKey(string scope)
    {
        try { return MonitoringSeriesEvaluator.DiskResourceKey(scope); }
        catch (ArgumentException) { return null; }
    }
}

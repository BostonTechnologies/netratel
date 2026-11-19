using NetRatel.Application.Monitoring;
using NetRatel.Application.Telemetry;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Infrastructure.Metrics;

internal sealed record MetricIngress(MonitoringEvidenceFence Fence, ulong Sequence, DateTimeOffset ReceivedAt,
    TelemetrySnapshot? Snapshot, MetricReading[]? Watches, int Bytes)
{
    public bool Partial => Snapshot is { } snapshot &&
        (snapshot.DiskInventoryTruncated || snapshot.NetworkQuality is MetricMeasurementQuality.Partial or
            MetricMeasurementQuality.Missing or MetricMeasurementQuality.Failed);
}

internal sealed record MetricReading(MetricHistoryKind Kind, string Key, string Label, DateTimeOffset At,
    MetricHistoryQuality Quality, double? Value = null, MetricMeasurementWindow? Window = null, ulong? Bytes = null,
    decimal? DiskTotal = null, decimal? DiskFree = null, Guid? Collection = null,
    ClientServiceState? State = null, ulong WatchRevision = 0)
{
    public DateTimeOffset OrderAt => Window?.AttributedEndUtc ?? At;
}

internal static class MetricHistoryObservations
{
    public static IEnumerable<MetricReading> Read(TelemetrySnapshot snapshot, bool windows)
    {
        var at = snapshot.ObservedAtUtc;
        var cpuQuality = Quality(snapshot.CpuQuality);
        if (windows && snapshot.CpuQuality == MetricMeasurementQuality.Legacy) cpuQuality |= MetricHistoryQuality.LegacyWindowsCpu;
        yield return new(MetricHistoryKind.CpuPercent, "cpu", "CPU", snapshot.Cpu?.Window?.EndedAtUtc ?? at,
            cpuQuality, snapshot.Cpu?.UsagePercent, snapshot.Cpu?.Window);

        var memoryQuality = snapshot.Memory is null
            ? snapshot.CpuQuality == MetricMeasurementQuality.Unsupported ? MetricHistoryQuality.Unsupported : MetricHistoryQuality.Missing
            : MetricHistoryQuality.None;
        yield return new(MetricHistoryKind.MemoryPercent, "memory", "Memory", at, memoryQuality, snapshot.Memory?.UsagePercent);
        yield return new(MetricHistoryKind.MemoryUsedBytes, "memory", "Used memory", at, memoryQuality, ToBytes(snapshot.Memory?.UsedMb));
        yield return new(MetricHistoryKind.MemoryTotalBytes, "memory", "Memory capacity", at, memoryQuality, ToBytes(snapshot.Memory?.TotalMb));

        var disks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var disk in snapshot.Disks)
        {
            if (disk.Metadata?.DefaultExclusionReason is not null || disk.CollectionId is not { } collection ||
                disk.CollectedAtUtc is not { } collected) continue;
            var scope = MonitoringObservationFactory.TryDiskKey(disk.Scope);
            if (scope is null) continue;
            var key = disk.Metadata?.CapacityKey ?? scope;
            if (!disks.Add(key)) continue;
            var complete = disk.CollectionQuality == TelemetryDiskCollectionQuality.Complete &&
                disk is { TotalBytes: > 0, FreeBytes: not null } && disk.FreeBytes <= disk.TotalBytes;
            var quality = complete ? MetricHistoryQuality.None : disk.CollectionQuality switch
            {
                TelemetryDiskCollectionQuality.Unsupported => MetricHistoryQuality.Unsupported,
                TelemetryDiskCollectionQuality.Failed => MetricHistoryQuality.Failed,
                _ => MetricHistoryQuality.Missing
            };
            if (disk.Metadata is null) quality |= MetricHistoryQuality.UnclassifiedDisk;
            if (snapshot.DiskInventoryTruncated) quality |= MetricHistoryQuality.InventoryPartial;
            var used = complete ? (double)((disk.TotalBytes!.Value - disk.FreeBytes!.Value) * 100m / disk.TotalBytes.Value) : (double?)null;
            yield return new(MetricHistoryKind.DiskUsedPercent, key, disk.Scope, collected, quality, used,
                DiskTotal: complete ? disk.TotalBytes : null, DiskFree: complete ? disk.FreeBytes : null, Collection: collection);
        }

        var partial = snapshot.NetworkQuality == MetricMeasurementQuality.Partial ? MetricHistoryQuality.InventoryPartial : MetricHistoryQuality.None;
        foreach (var network in snapshot.Networks)
        {
            var evidence = network.Evidence;
            var key = evidence?.InterfaceId.ToUpperInvariant() ?? "legacy:" + network.Scope;
            var label = evidence?.DisplayName ?? network.Scope;
            var quality = Quality(evidence?.Quality ?? MetricMeasurementQuality.Legacy) | partial;
            var window = evidence?.Window;
            var observedAt = window?.EndedAtUtc ?? at;
            yield return Network(MetricHistoryKind.NetworkRx, key, label, observedAt, quality, window,
                evidence?.RxBytes, evidence is null ? network.RxBytesPerSec : null);
            yield return Network(MetricHistoryKind.NetworkTx, key, label, observedAt, quality, window,
                evidence?.TxBytes, evidence is null ? network.TxBytesPerSec : null);
        }
    }

    public static string CanonicalServiceName(ClientServicePlatform platform, string name) =>
        platform == ClientServicePlatform.Windows ? name.ToUpperInvariant() : name;

    public static MetricReading Service(ClientServiceObservation service, ulong revision) => new(MetricHistoryKind.ServiceState,
        $"{(int)service.Platform}:{CanonicalServiceName(service.Platform, service.Name)}", service.Name, service.ObservedAtUtc,
        service.State switch
        {
            ClientServiceState.Unsupported => MetricHistoryQuality.Unsupported,
            ClientServiceState.Missing => MetricHistoryQuality.Missing,
            ClientServiceState.Unknown => MetricHistoryQuality.Missing,
            _ => MetricHistoryQuality.None
        }, State: service.State, WatchRevision: revision);

    private static MetricReading Network(MetricHistoryKind kind, string key, string label, DateTimeOffset at,
        MetricHistoryQuality quality, MetricMeasurementWindow? window, ulong? bytes, double? legacyRate) =>
        new(kind, key, label, at, quality, bytes is not null && window is not null
            ? bytes.Value * 1_000_000d / window.ElapsedMicroseconds : legacyRate, window, bytes);

    private static double? ToBytes(double? mib) => mib is >= 0 and <= (ulong.MaxValue / 1_048_576d) ? mib * 1_048_576d : null;

    private static MetricHistoryQuality Quality(MetricMeasurementQuality quality) => quality switch
    {
        MetricMeasurementQuality.Valid => MetricHistoryQuality.None,
        MetricMeasurementQuality.Legacy => MetricHistoryQuality.Legacy,
        MetricMeasurementQuality.WarmUp => MetricHistoryQuality.WarmUp,
        MetricMeasurementQuality.Reset => MetricHistoryQuality.Reset,
        MetricMeasurementQuality.Unsupported => MetricHistoryQuality.Unsupported,
        MetricMeasurementQuality.Failed => MetricHistoryQuality.Failed,
        MetricMeasurementQuality.Partial => MetricHistoryQuality.InventoryPartial,
        _ => MetricHistoryQuality.Missing
    };
}

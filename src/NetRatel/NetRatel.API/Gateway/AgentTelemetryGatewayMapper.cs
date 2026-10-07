using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using ApplicationTelemetryCpu = NetRatel.Application.Telemetry.TelemetryCpu;
using ApplicationTelemetryDisk = NetRatel.Application.Telemetry.TelemetryDisk;
using ApplicationTelemetryMemory = NetRatel.Application.Telemetry.TelemetryMemory;
using ApplicationTelemetryNetwork = NetRatel.Application.Telemetry.TelemetryNetwork;
using ApplicationTelemetryTransportHealth = NetRatel.Application.Telemetry.TelemetryTransportHealth;

namespace NetRatel.API.Gateway;

internal static class AgentTelemetryGatewayMapper
{
    public static TelemetrySnapshot MapSnapshot(
        TelemetryFrame frame,
        ClientKey client,
        long connectionEpoch,
        DateTimeOffset receivedAtUtc) =>
        new(
            client,
            connectionEpoch,
            frame.Sequence,
            frame.ObservedAtUtc.ToDateTimeOffset(),
            receivedAtUtc,
            frame.Cpu is null
                ? null
                : new ApplicationTelemetryCpu(
                    frame.Cpu.UsagePercent,
                    frame.Cpu.HasLoadAverage ? frame.Cpu.LoadAverage : null,
                    frame.Cpu.HasProcessCount ? frame.Cpu.ProcessCount : null),
            frame.Memory is null
                ? null
                : new ApplicationTelemetryMemory(
                    frame.Memory.TotalMb,
                    frame.Memory.UsedMb,
                    frame.Memory.AvailableMb,
                    frame.Memory.UsagePercent),
            frame.Disks.Select(disk => new ApplicationTelemetryDisk(
                disk.Scope.Trim(),
                disk.TotalGb,
                disk.UsedGb,
                disk.FreeGb,
                disk.UsagePercent,
                disk.HasTotalBytes ? disk.TotalBytes : null,
                disk.HasFreeBytes ? disk.FreeBytes : null,
                Guid.TryParseExact(disk.CollectionId, "D", out var collectionId) && collectionId != Guid.Empty ? collectionId : null,
                disk.CollectedAtUtc?.ToDateTimeOffset(),
                (global::NetRatel.Application.Telemetry.TelemetryDiskCollectionQuality)(int)disk.CollectionQuality)).ToArray(),
            frame.Networks.Select(network => new ApplicationTelemetryNetwork(
                network.Scope.Trim(),
                network.RxBytesPerSec,
                network.TxBytesPerSec)).ToArray(),
            frame.TransportHealth is null
                ? null
                : new ApplicationTelemetryTransportHealth(
                    frame.TransportHealth.UptimeSeconds,
                    NullIfWhiteSpace(frame.TransportHealth.AgentVersion),
                    NullIfWhiteSpace(frame.TransportHealth.OsVersion),
                    frame.TransportHealth.LastHeartbeat?.ToDateTimeOffset()),
            "akka",
            IsAuthoritative: true);

    public static void ThrowIfInvalid(AgentFrameValidationResult validation)
    {
        if (!validation.IsValid)
        {
            throw new RpcException(new Status(validation.StatusCode, validation.Error));
        }
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

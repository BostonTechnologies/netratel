using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;

namespace NetRatel.API.Gateway;

public static class AgentTelemetryProtocolValidator
{
    public static AgentFrameValidationResult Validate(
        TelemetryFrame frame,
        AuthenticatedAgentIdentity identity,
        string supportedProtocolVersion,
        int maxScopesPerFrame,
        DateTimeOffset? receivedAtUtc = null)
    {
        if (!string.Equals(frame.ProtocolVersion, supportedProtocolVersion, StringComparison.Ordinal))
        {
            return Invalid(
                StatusCode.FailedPrecondition,
                $"Unsupported protocol version '{frame.ProtocolVersion}'.");
        }

        if (frame.TenantId != identity.TenantId ||
            !Guid.TryParse(frame.ClientId, out var clientId) ||
            clientId != identity.AgentId)
        {
            return Invalid(
                StatusCode.PermissionDenied,
                "Frame tenant_id/client_id does not match the authenticated principal.");
        }

        if (!Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty)
        {
            return Invalid(StatusCode.InvalidArgument, "connection_id must contain a non-empty GUID.");
        }

        if (frame.ConnectionEpoch == 0 || frame.ConnectionEpoch > long.MaxValue)
        {
            return Invalid(StatusCode.InvalidArgument, "connection_epoch must contain a positive signed 64-bit value.");
        }

        if (frame.Sequence == 0)
        {
            return Invalid(StatusCode.InvalidArgument, "Telemetry sequence numbers must start at one.");
        }

        if (!IsValidTimestamp(frame.ObservedAtUtc))
        {
            return Invalid(StatusCode.InvalidArgument, "observed_at_utc must contain a valid timestamp.");
        }

        if (frame.Cpu is null && frame.Memory is null && frame.Disks.Count == 0 &&
            frame.Networks.Count == 0 && frame.TransportHealth is null)
        {
            return Invalid(StatusCode.InvalidArgument, "At least one supported telemetry value is required.");
        }

        if (frame.Disks.Count > maxScopesPerFrame || frame.Networks.Count > maxScopesPerFrame)
        {
            return Invalid(StatusCode.ResourceExhausted, "Telemetry scope count exceeds the configured limit.");
        }

        var metricValidation = ValidateCpu(frame.Cpu);
        if (!metricValidation.IsValid)
        {
            return metricValidation;
        }

        metricValidation = ValidateMemory(frame.Memory);
        if (!metricValidation.IsValid)
        {
            return metricValidation;
        }

        metricValidation = ValidateDisks(frame.Disks, frame.ObservedAtUtc.ToDateTimeOffset(), receivedAtUtc ?? TimeProvider.System.GetUtcNow());
        if (!metricValidation.IsValid)
        {
            return metricValidation;
        }

        metricValidation = ValidateNetworks(frame.Networks);
        if (!metricValidation.IsValid)
        {
            return metricValidation;
        }

        return ValidateTransportHealth(frame.TransportHealth);
    }

    private static AgentFrameValidationResult ValidateCpu(TelemetryCpu? cpu)
    {
        if (cpu is not null &&
            (!IsPercentage(cpu.UsagePercent) ||
             (cpu.HasLoadAverage && !IsNonNegativeFinite(cpu.LoadAverage)) ||
             (cpu.HasProcessCount && cpu.ProcessCount < 0)))
        {
            return Invalid(StatusCode.InvalidArgument, "CPU telemetry contains an unsupported value.");
        }

        return AgentFrameValidationResult.Success;
    }

    private static AgentFrameValidationResult ValidateMemory(TelemetryMemory? memory)
    {
        if (memory is not null &&
            (!IsNonNegativeFinite(memory.TotalMb) ||
             !IsNonNegativeFinite(memory.UsedMb) ||
             !IsNonNegativeFinite(memory.AvailableMb) ||
             !IsPercentage(memory.UsagePercent)))
        {
            return Invalid(StatusCode.InvalidArgument, "Memory telemetry contains an unsupported value.");
        }

        return AgentFrameValidationResult.Success;
    }

    private static AgentFrameValidationResult ValidateDisks(IEnumerable<TelemetryDisk> disks, DateTimeOffset envelopeObservedAtUtc, DateTimeOffset receivedAtUtc)
    {
        var diskArray = disks as TelemetryDisk[] ?? disks.ToArray();
        if (!HaveValidUniqueScopes(diskArray.Select(disk => disk.Scope)) ||
            diskArray.Any(disk =>
                !IsNonNegativeFinite(disk.TotalGb) ||
                !IsNonNegativeFinite(disk.UsedGb) ||
                !IsNonNegativeFinite(disk.FreeGb) ||
                !IsPercentage(disk.UsagePercent) ||
                disk.HasTotalBytes != disk.HasFreeBytes ||
                (disk.HasTotalBytes && (disk.TotalBytes == 0 || disk.FreeBytes > disk.TotalBytes)) ||
                !HasValidDiskCollection(disk, envelopeObservedAtUtc, receivedAtUtc)))
        {
            return Invalid(StatusCode.InvalidArgument, "Disk telemetry contains an unsupported value or duplicate scope.");
        }

        return AgentFrameValidationResult.Success;
    }

    private static bool HasValidDiskCollection(TelemetryDisk disk, DateTimeOffset envelopeObservedAtUtc, DateTimeOffset receivedAtUtc)
    {
        var collectedAtUtc = disk.CollectedAtUtc;
        // Old clients remain transport compatible, but absent proof cannot become complete Monitoring evidence.
        if (disk.CollectionId.Length == 0 && collectedAtUtc is null &&
            disk.CollectionQuality == TelemetryDiskCollectionQuality.Unspecified) return true;
        if (!Guid.TryParseExact(disk.CollectionId, "D", out var id) || id == Guid.Empty ||
            collectedAtUtc is null || !IsValidTimestamp(collectedAtUtc) ||
            collectedAtUtc.ToDateTimeOffset() == default ||
            collectedAtUtc.ToDateTimeOffset() > envelopeObservedAtUtc ||
            collectedAtUtc.ToDateTimeOffset() > receivedAtUtc ||
            (int)disk.CollectionQuality is < 1 or > 4) return false;
        return disk.CollectionQuality != TelemetryDiskCollectionQuality.Complete ||
            disk.HasTotalBytes && disk.HasFreeBytes;
    }

    private static AgentFrameValidationResult ValidateNetworks(IEnumerable<TelemetryNetwork> networks)
    {
        var networkArray = networks as TelemetryNetwork[] ?? networks.ToArray();
        if (!HaveValidUniqueScopes(networkArray.Select(network => network.Scope)) ||
            networkArray.Any(network =>
                !IsNonNegativeFinite(network.RxBytesPerSec) ||
                !IsNonNegativeFinite(network.TxBytesPerSec)))
        {
            return Invalid(StatusCode.InvalidArgument, "Network telemetry contains an unsupported value or duplicate scope.");
        }

        return AgentFrameValidationResult.Success;
    }

    private static AgentFrameValidationResult ValidateTransportHealth(TelemetryTransportHealth? health)
    {
        if (health is not null &&
            (health.UptimeSeconds < 0 ||
             health.AgentVersion.Length > 128 ||
             health.OsVersion.Length > 512 ||
             (health.LastHeartbeat is not null && !IsValidTimestamp(health.LastHeartbeat))))
        {
            return Invalid(StatusCode.InvalidArgument, "Transport health telemetry contains an unsupported value.");
        }

        return AgentFrameValidationResult.Success;
    }

    private static bool HaveValidUniqueScopes(IEnumerable<string> scopes)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in scopes)
        {
            if (string.IsNullOrWhiteSpace(scope) || scope.Length > 128 || !normalized.Add(scope.Trim()))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidTimestamp(Google.Protobuf.WellKnownTypes.Timestamp? timestamp)
    {
        if (timestamp is null)
        {
            return false;
        }

        try
        {
            _ = timestamp.ToDateTimeOffset();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsPercentage(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 100;

    private static bool IsNonNegativeFinite(double value) =>
        double.IsFinite(value) && value >= 0;

    private static AgentFrameValidationResult Invalid(StatusCode statusCode, string error) =>
        new(false, statusCode, error);
}

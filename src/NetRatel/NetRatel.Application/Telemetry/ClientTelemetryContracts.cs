using NetRatel.Application.Presence;

namespace NetRatel.Application.Telemetry;

public sealed record TelemetryCpu(
    double UsagePercent,
    double? LoadAverage,
    int? ProcessCount);

public sealed record TelemetryMemory(
    double TotalMb,
    double UsedMb,
    double AvailableMb,
    double UsagePercent);

public sealed record TelemetryDisk(
    string Scope,
    double TotalGb,
    double UsedGb,
    double FreeGb,
    double UsagePercent);

public sealed record TelemetryNetwork(
    string Scope,
    double RxBytesPerSec,
    double TxBytesPerSec);

public sealed record TelemetryTransportHealth(
    long UptimeSeconds,
    string? AgentVersion,
    string? OsVersion,
    DateTimeOffset? LastHeartbeat);

public sealed record TelemetrySnapshot(
    ClientKey Client,
    long ConnectionEpoch,
    ulong Sequence,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    TelemetryCpu? Cpu,
    TelemetryMemory? Memory,
    IReadOnlyList<TelemetryDisk> Disks,
    IReadOnlyList<TelemetryNetwork> Networks,
    TelemetryTransportHealth? TransportHealth,
    string Source = "akka-shadow",
    bool IsAuthoritative = false);

public enum TelemetryMessageDisposition
{
    Accepted = 0,
    Duplicate = 1,
    StaleConnectionEpoch = 2,
    StaleSequence = 3
}

public interface IClientTelemetryMessage
{
    ClientKey Client { get; }
}

public sealed record RecordTelemetrySnapshot(TelemetrySnapshot Snapshot) : IClientTelemetryMessage
{
    public ClientKey Client => Snapshot.Client;
}

public sealed record GetClientTelemetry(ClientKey Client) : IClientTelemetryMessage;

public sealed record GetClientTelemetryReadModel;

public sealed record TelemetryMessageResult(
    ClientKey Client,
    TelemetryMessageDisposition Disposition,
    ulong LastAcceptedSequence);

public sealed record ClientTelemetryState(
    ClientKey Client,
    TelemetrySnapshot? Latest);

public sealed record ClientTelemetryReadModelSnapshot(
    IReadOnlyList<TelemetrySnapshot> Snapshots,
    DateTimeOffset GeneratedAtUtc);

public sealed record ProbeClientTelemetryRoute;

public sealed record ClientTelemetryRouteStatus(
    int ActiveTelemetryClients,
    ulong AcceptedCount,
    ulong RejectedCount,
    DateTimeOffset? LastUpdateTimestamp,
    DateTimeOffset StartedAtUtc,
    string Mode,
    string Authority);

public interface IClientTelemetryRouter
{
    Task<TelemetryMessageResult> RecordAsync(
        RecordTelemetrySnapshot message,
        CancellationToken cancellationToken);

    Task<ClientTelemetryState> GetSnapshotAsync(
        ClientKey client,
        CancellationToken cancellationToken);

    Task<ClientTelemetryReadModelSnapshot> GetReadModelAsync(CancellationToken cancellationToken);

    Task<ClientTelemetryRouteStatus> ProbeAsync(CancellationToken cancellationToken);
}

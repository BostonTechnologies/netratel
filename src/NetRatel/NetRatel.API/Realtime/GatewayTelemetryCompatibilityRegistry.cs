using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NetRatel.Application.Telemetry;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Realtime;

/// <summary>
/// Bounded V1 DTO compatibility read model fed by authenticated gateway
/// telemetry snapshots. It contains no Spacetime identity, subscription, or
/// projection dependency.
/// </summary>
public interface IAgentTelemetryCompatibilityRegistry
{
    void Upsert(TelemetrySnapshot snapshot);
    AgentTelemetrySnapshotDto? GetSnapshot(string clientIdentity);
    IReadOnlyList<AgentTelemetrySnapshotDto> GetSnapshots();
    IAsyncEnumerable<AgentTelemetrySnapshotDto> StreamAsync(CancellationToken cancellationToken);
}

public sealed class GatewayTelemetryCompatibilityRegistry : IAgentTelemetryCompatibilityRegistry
{
    private const int MaxHistory = 60;
    private readonly ConcurrentDictionary<string, AgentTelemetryState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Channel<AgentTelemetrySnapshotDto>> _subscribers = new();

    public void Upsert(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var clientIdentity = snapshot.Client.AgentId.ToString("D");
        var state = _states.GetOrAdd(clientIdentity, static identity => new AgentTelemetryState(identity));
        AgentTelemetrySnapshotDto compatibility;
        lock (state.Sync)
        {
            state.LastUpdatedUtc = snapshot.ObservedAtUtc;
            state.Cpu = snapshot.Cpu is null ? null : new AgentCpuTelemetryDto(snapshot.Cpu.UsagePercent, snapshot.Cpu.LoadAverage, snapshot.Cpu.ProcessCount);
            state.Memory = snapshot.Memory is null ? null : new AgentMemoryTelemetryDto(
                checked((long)Math.Round(snapshot.Memory.TotalMb)), checked((long)Math.Round(snapshot.Memory.UsedMb)),
                checked((long)Math.Round(snapshot.Memory.AvailableMb)), snapshot.Memory.UsagePercent);
            state.Disks = snapshot.Disks.Select(static disk => new AgentDiskTelemetryDto(disk.Scope, disk.TotalGb, disk.UsedGb, disk.FreeGb, disk.UsagePercent)).OrderBy(static disk => disk.Scope, StringComparer.OrdinalIgnoreCase).ToArray();
            state.Networks = snapshot.Networks.Select(static network => new AgentNetworkTelemetryDto(network.Scope, network.RxBytesPerSec, network.TxBytesPerSec)).OrderBy(static network => network.Scope, StringComparer.OrdinalIgnoreCase).ToArray();
            state.Health = snapshot.TransportHealth is null ? null : new AgentHealthTelemetryDto(snapshot.TransportHealth.UptimeSeconds, snapshot.TransportHealth.AgentVersion, snapshot.TransportHealth.OsVersion, snapshot.TransportHealth.LastHeartbeat);

            if (state.Cpu is not null) Enqueue(state.CpuHistory, snapshot.ObservedAtUtc, state.Cpu.UsagePercent);
            if (state.Memory is not null) Enqueue(state.MemoryHistory, snapshot.ObservedAtUtc, state.Memory.UsagePercent);
            if (state.Networks.Count > 0)
            {
                Enqueue(state.NetworkRxHistory, snapshot.ObservedAtUtc, state.Networks.Sum(static network => network.RxBytesPerSec));
                Enqueue(state.NetworkTxHistory, snapshot.ObservedAtUtc, state.Networks.Sum(static network => network.TxBytesPerSec));
            }
            compatibility = BuildSnapshot(state);
        }

        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(compatibility);
        }
    }

    public AgentTelemetrySnapshotDto? GetSnapshot(string clientIdentity)
    {
        if (!_states.TryGetValue(clientIdentity, out var state)) return null;
        lock (state.Sync) return BuildSnapshot(state);
    }

    public IReadOnlyList<AgentTelemetrySnapshotDto> GetSnapshots() => _states.Values.Select(state =>
    {
        lock (state.Sync) return BuildSnapshot(state);
    }).OrderBy(static snapshot => snapshot.ClientIdentity, StringComparer.OrdinalIgnoreCase).ToArray();

    public IAsyncEnumerable<AgentTelemetrySnapshotDto> StreamAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<AgentTelemetrySnapshotDto>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return ReadAsync(id, channel, cancellationToken);
    }

    private async IAsyncEnumerable<AgentTelemetrySnapshotDto> ReadAsync(Guid id, Channel<AgentTelemetrySnapshotDto> channel, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return snapshot;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    private static void Enqueue(Queue<AgentTelemetryPointDto> history, DateTimeOffset timestamp, double value)
    {
        history.Enqueue(new AgentTelemetryPointDto(timestamp, Math.Round(value, 1)));
        while (history.Count > MaxHistory) history.Dequeue();
    }

    private static AgentTelemetrySnapshotDto BuildSnapshot(AgentTelemetryState state) => new(
        state.ClientIdentity, state.LastUpdatedUtc ?? DateTimeOffset.UtcNow, state.LastUpdatedUtc, state.Cpu, state.Memory,
        state.Disks, state.Networks, state.Health, state.CpuHistory.ToArray(), state.MemoryHistory.ToArray(),
        state.NetworkRxHistory.ToArray(), state.NetworkTxHistory.ToArray());

    private sealed class AgentTelemetryState(string clientIdentity)
    {
        public object Sync { get; } = new();
        public string ClientIdentity { get; } = clientIdentity;
        public DateTimeOffset? LastUpdatedUtc { get; set; }
        public AgentCpuTelemetryDto? Cpu { get; set; }
        public AgentMemoryTelemetryDto? Memory { get; set; }
        public IReadOnlyList<AgentDiskTelemetryDto> Disks { get; set; } = [];
        public IReadOnlyList<AgentNetworkTelemetryDto> Networks { get; set; } = [];
        public AgentHealthTelemetryDto? Health { get; set; }
        public Queue<AgentTelemetryPointDto> CpuHistory { get; } = new();
        public Queue<AgentTelemetryPointDto> MemoryHistory { get; } = new();
        public Queue<AgentTelemetryPointDto> NetworkRxHistory { get; } = new();
        public Queue<AgentTelemetryPointDto> NetworkTxHistory { get; } = new();
    }
}

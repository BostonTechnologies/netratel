using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;

namespace NetRatel.API.Realtime;

public sealed record GatewayTelemetryLiveSnapshot(
    TelemetrySnapshot Snapshot,
    long Revision);

public sealed record GatewayTelemetryLiveMode(
    string ConnectionState,
    bool SupportsDynamicSampling,
    bool InteractiveRequested,
    bool InteractiveEffective,
    int EffectiveSamplePeriodMilliseconds,
    string? AgentVersion,
    long PolicyRevision,
    DateTimeOffset PolicyExpiresAtUtc,
    string StateReason);

public sealed record GatewayTelemetryLiveUpdate(
    GatewayTelemetryLiveSnapshot? Snapshot,
    GatewayTelemetryLiveMode? Mode,
    long Revision);

public interface IGatewayTelemetryLiveRegistry
{
    void PublishAccepted(TelemetrySnapshot snapshot);
    void PublishMode(ClientKey client, GatewayTelemetryLiveMode mode);
    GatewayTelemetryLiveSubscription Subscribe(ClientKey client);
    GatewayTelemetryLiveSnapshot? GetLatest(ClientKey client);
    GatewayTelemetryLiveMode? GetMode(ClientKey client);
}

/// <summary>Process-local V2 fanout. Akka remains the source of accepted state.</summary>
public sealed class GatewayTelemetryLiveRegistry : IGatewayTelemetryLiveRegistry
{
    private readonly ConcurrentDictionary<ClientKey, ClientState> _states = [];

    public void PublishAccepted(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // The authoritative router supplies the initial snapshot for a new SSE
        // connection. Keep this process-local fanout state only while a browser
        // is subscribed so disconnected agents cannot accumulate indefinitely.
        if (!_states.TryGetValue(snapshot.Client, out var state)) return;
        GatewayTelemetryLiveUpdate? published = null;
        Channel<GatewayTelemetryLiveUpdate>[] subscribers;
        lock (state.Gate)
        {
            if (state.Latest is { Snapshot: var current } && !IsNewer(snapshot, current))
            {
                return;
            }

            state.Revision++;
            state.Latest = new GatewayTelemetryLiveSnapshot(Clone(snapshot), state.Revision);
            published = new GatewayTelemetryLiveUpdate(state.Latest, null, state.Revision);
            subscribers = state.Subscribers.Values.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryWrite(published);
        }
    }

    public void PublishMode(ClientKey client, GatewayTelemetryLiveMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        // A subscriber receives the current mode during endpoint initialization;
        // mode-only state does not need to outlive all subscriptions.
        if (!_states.TryGetValue(client, out var state)) return;
        GatewayTelemetryLiveUpdate published;
        Channel<GatewayTelemetryLiveUpdate>[] subscribers;
        lock (state.Gate)
        {
            if (state.Mode == mode) return;
            state.Revision++;
            state.Mode = mode;
            published = new GatewayTelemetryLiveUpdate(null, mode, state.Revision);
            subscribers = state.Subscribers.Values.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryWrite(published);
        }
    }

    public GatewayTelemetryLiveSubscription Subscribe(ClientKey client)
    {
        var state = _states.GetOrAdd(client, static _ => new ClientState());
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<GatewayTelemetryLiveUpdate>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        lock (state.Gate)
        {
            state.Subscribers.Add(id, channel);
        }

        return new GatewayTelemetryLiveSubscription(channel.Reader, () =>
        {
            lock (state.Gate)
            {
                if (state.Subscribers.Remove(id, out var subscriber))
                {
                    subscriber.Writer.TryComplete();
                }
                if (state.Subscribers.Count == 0)
                {
                    ((ICollection<KeyValuePair<ClientKey, ClientState>>)_states).Remove(new(client, state));
                }
            }
        });
    }

    public GatewayTelemetryLiveSnapshot? GetLatest(ClientKey client)
    {
        if (!_states.TryGetValue(client, out var state)) return null;
        lock (state.Gate) return state.Latest;
    }

    public GatewayTelemetryLiveMode? GetMode(ClientKey client)
    {
        if (!_states.TryGetValue(client, out var state)) return null;
        lock (state.Gate) return state.Mode;
    }

    private static bool IsNewer(TelemetrySnapshot candidate, TelemetrySnapshot current) =>
        candidate.ConnectionEpoch > current.ConnectionEpoch ||
        candidate.ConnectionEpoch == current.ConnectionEpoch && candidate.Sequence > current.Sequence;

    private static TelemetrySnapshot Clone(TelemetrySnapshot snapshot) => snapshot with
    {
        Disks = snapshot.Disks.ToArray(),
        Networks = snapshot.Networks.ToArray()
    };

    private sealed class ClientState
    {
        public object Gate { get; } = new();
        public long Revision { get; set; }
        public GatewayTelemetryLiveSnapshot? Latest { get; set; }
        public GatewayTelemetryLiveMode? Mode { get; set; }
        public Dictionary<Guid, Channel<GatewayTelemetryLiveUpdate>> Subscribers { get; } = [];
    }
}

public sealed class GatewayTelemetryLiveSubscription(
    ChannelReader<GatewayTelemetryLiveUpdate> reader,
    Action release) : IAsyncDisposable
{
    private int _disposed;
    public ChannelReader<GatewayTelemetryLiveUpdate> Reader { get; } = reader;
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) release();
        return ValueTask.CompletedTask;
    }
}

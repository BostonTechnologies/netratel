using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using NetRatel.API.Realtime;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

public interface IAgentTelemetryGatewaySessionRegistry
{
    AgentTelemetryGatewaySessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion);
    AgentTelemetryGatewaySessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion, bool provisional) =>
        Register(client, connectionId, connectionEpoch, supportsDynamicSampling, agentVersion);
    AgentTelemetryGatewaySessionStatus GetStatus(ClientKey client);
    void PublishPolicy(ClientKey client, TelemetrySamplingPolicyState policy);
}

public sealed record AgentTelemetryGatewaySessionStatus(
    bool Connected,
    bool SupportsDynamicSampling,
    string? AgentVersion,
    long? ConnectionEpoch);

public sealed class AgentTelemetryGatewaySessionRegistry : IAgentTelemetryGatewaySessionRegistry
{
    private readonly ConcurrentDictionary<ClientKey, Session> _sessions = [];
    private readonly object _registrationGate = new();
    private readonly ITelemetryInteractiveDemandRegistry _demand;
    private readonly IGatewayTelemetryLiveRegistry _live;

    public AgentTelemetryGatewaySessionRegistry(ITelemetryInteractiveDemandRegistry demand, IGatewayTelemetryLiveRegistry live)
    {
        _demand = demand;
        _live = live;
        _demand.PolicyChanged += PublishPolicy;
    }

    public AgentTelemetryGatewaySessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion) =>
        Register(client, connectionId, connectionEpoch, supportsDynamicSampling, agentVersion, provisional: false);

    public AgentTelemetryGatewaySessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion, bool provisional)
    {
        var replacement = new Session(client, connectionId, connectionEpoch, supportsDynamicSampling, agentVersion);
        lock (_registrationGate)
        {
            while (true)
            {
                if (_sessions.TryGetValue(client, out var current))
                {
                    if (!GatewaySessionRegistrationFence.CanReplace(connectionId, connectionEpoch, current.ConnectionId, current.ConnectionEpoch))
                    {
                        replacement.Complete();
                        throw new AgentGatewayRegistrationFencedException();
                    }
                    if (_sessions.TryUpdate(client, replacement, current))
                    {
                        current.Complete();
                        break;
                    }
                    continue;
                }
                if (_sessions.TryAdd(client, replacement)) break;
            }

            if (!provisional) Activate(client, replacement);
        }
        return new AgentTelemetryGatewaySessionRegistration(replacement, () => Unregister(client, replacement),
            () => IsCurrent(client, replacement), () => Activate(client, replacement),
            action => TryPublish(client, replacement, action));
    }

    private bool IsCurrent(ClientKey client, Session session) =>
        _sessions.TryGetValue(client, out var current) && ReferenceEquals(current, session) && !session.CompletionToken.IsCancellationRequested;

    private bool Activate(ClientKey client, Session session)
    {
        lock (_registrationGate)
        {
            if (!IsCurrent(client, session)) return false;
            session.Active = true;
            var policy = _demand.GetPolicy(client);
            session.PublishPolicy(policy);
            PublishMode(client, session, policy);
            return true;
        }
    }

    // Publication and replacement share this short critical section: cancellation
    // alone cannot fence processing that already resumed from the durable router.
    private bool TryPublish(ClientKey client, Session session, Action publish)
    {
        lock (_registrationGate)
        {
            if (!IsCurrent(client, session) || !session.Active) return false;
            publish();
            return true;
        }
    }

    public AgentTelemetryGatewaySessionStatus GetStatus(ClientKey client)
    {
        lock (_registrationGate)
        {
            return _sessions.TryGetValue(client, out var session) && session.Active
                ? new(true, session.SupportsDynamicSampling, session.AgentVersion, checked((long)session.ConnectionEpoch))
                : new(false, false, null, null);
        }
    }

    public void PublishPolicy(ClientKey client, TelemetrySamplingPolicyState policy)
    {
        lock (_registrationGate)
        {
            if (_sessions.TryGetValue(client, out var session) && session.Active)
            {
                session.PublishPolicy(policy);
                PublishMode(client, session, policy);
            }
        }
    }

    private void Unregister(ClientKey client, Session session)
    {
        lock (_registrationGate)
        {
            if (((ICollection<KeyValuePair<ClientKey, Session>>)_sessions).Remove(new(client, session)))
            {
                session.Complete();
                var policy = _demand.GetPolicy(client);
                _live.PublishMode(client, new GatewayTelemetryLiveMode(
                    "Offline", false, policy.Interactive, false, 5000, null,
                    policy.Revision, policy.ExpiresAtUtc, "telemetry-session-disconnected"));
            }
        }
    }

    private void PublishMode(ClientKey client, Session session, TelemetrySamplingPolicyState policy)
    {
        var effective = session.SupportsDynamicSampling && policy.Interactive;
        _live.PublishMode(client, new GatewayTelemetryLiveMode(
            session.SupportsDynamicSampling ? "Live" : "Unsupported",
            session.SupportsDynamicSampling,
            policy.Interactive,
            effective,
            effective ? policy.FastIntervalMilliseconds : 5000,
            session.AgentVersion,
            policy.Revision,
            policy.ExpiresAtUtc,
            effective ? policy.Reason : session.SupportsDynamicSampling ? "baseline" : "client-upgrade-required"));
    }

    internal sealed class Session(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion)
    {
        private readonly Channel<GatewayTelemetryFrame> _reliable = Channel.CreateBounded<GatewayTelemetryFrame>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        private readonly Channel<byte> _wakeups = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        private readonly object _policyGate = new();
        private readonly CancellationTokenSource _completion = new();
        private TelemetrySamplingPolicyState? _latestPolicy;
        private long _lastPolicyRevision = -1;
        private int _completed;

        public Guid RegistrationId { get; } = Guid.NewGuid();
        public bool Active { get; set; }
        public ClientKey Client { get; } = client;
        public Guid ConnectionId { get; } = connectionId;
        public ulong ConnectionEpoch { get; } = connectionEpoch;
        public bool SupportsDynamicSampling { get; } = supportsDynamicSampling;
        public string AgentVersion { get; } = agentVersion;
        public CancellationToken CompletionToken => _completion.Token;

        public async ValueTask EnqueueReliableAsync(GatewayTelemetryFrame frame, CancellationToken cancellationToken)
        {
            await _reliable.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            Pulse();
        }

        public void PublishPolicy(TelemetrySamplingPolicyState policy)
        {
            if (!SupportsDynamicSampling || Volatile.Read(ref _completed) != 0) return;
            lock (_policyGate)
            {
                if (policy.Revision <= _lastPolicyRevision || Volatile.Read(ref _completed) != 0) return;
                _lastPolicyRevision = policy.Revision;
                _latestPolicy = policy;
            }
            Pulse();
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _completion.Cancel();
            _reliable.Writer.TryComplete();
            _wakeups.Writer.TryComplete();
        }

        public async IAsyncEnumerable<GatewayTelemetryFrame> ReadOutboundAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                while (_reliable.Reader.TryRead(out var reliable)) yield return reliable;
                TelemetrySamplingPolicyState? policy;
                lock (_policyGate)
                {
                    policy = _latestPolicy;
                    _latestPolicy = null;
                }
                if (policy is not null)
                {
                    yield return ToFrame(policy);
                    continue;
                }

                var completed = false;
                try
                {
                    await _wakeups.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    while (_wakeups.Reader.TryRead(out _)) { }
                }
                catch (ChannelClosedException)
                {
                    completed = true;
                }
                if (!completed) continue;

                while (_reliable.Reader.TryRead(out var finalReliable)) yield return finalReliable;
                lock (_policyGate)
                {
                    policy = _latestPolicy;
                    _latestPolicy = null;
                }
                if (policy is not null) yield return ToFrame(policy);
                yield break;
            }
        }

        private void Pulse() => _wakeups.Writer.TryWrite(0);

        private GatewayTelemetryFrame ToFrame(TelemetrySamplingPolicyState policy) => new()
        {
            ProtocolVersion = "1.0",
            TenantId = Client.TenantId,
            ClientId = Client.AgentId.ToString("D"),
            ConnectionEpoch = ConnectionEpoch,
            ConnectionId = ConnectionId.ToString("D"),
            Sequence = 0,
            TelemetrySamplingPolicy = new TelemetrySamplingPolicy
            {
                Revision = checked((ulong)policy.Revision),
                FastIntervalMilliseconds = checked((uint)policy.FastIntervalMilliseconds),
                SlowIntervalSeconds = checked((uint)policy.SlowIntervalSeconds),
                Interactive = policy.Interactive,
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(policy.ExpiresAtUtc),
                Reason = policy.Reason
            }
        };
    }
}

public sealed class AgentTelemetryGatewaySessionRegistration : IAsyncDisposable
{
    private readonly AgentTelemetryGatewaySessionRegistry.Session _session;
    private readonly Action _unregister;
    private readonly Func<bool> _isCurrent;
    private readonly Func<bool> _tryActivate;
    private readonly Func<Action, bool> _tryPublish;
    private int _disposed;

    internal AgentTelemetryGatewaySessionRegistration(AgentTelemetryGatewaySessionRegistry.Session session, Action unregister, Func<bool> isCurrent, Func<bool> tryActivate, Func<Action, bool> tryPublish)
    {
        _session = session;
        _unregister = unregister;
        _isCurrent = isCurrent;
        _tryActivate = tryActivate;
        _tryPublish = tryPublish;
    }

    public Guid RegistrationId => _session.RegistrationId;
    public bool IsCurrent => Volatile.Read(ref _disposed) == 0 && _isCurrent();
    public bool TryActivate() => IsCurrent && _tryActivate();
    public bool TryPublish(Action publish) => IsCurrent && _tryPublish(publish);
    public bool SupportsDynamicSampling => _session.SupportsDynamicSampling;
    public CancellationToken CompletionToken => _session.CompletionToken;
    public ValueTask EnqueueReliableAsync(GatewayTelemetryFrame frame, CancellationToken cancellationToken) => _session.EnqueueReliableAsync(frame, cancellationToken);
    public IAsyncEnumerable<GatewayTelemetryFrame> ReadOutboundAsync(CancellationToken cancellationToken) => _session.ReadOutboundAsync(cancellationToken);
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _unregister();
        return ValueTask.CompletedTask;
    }
}

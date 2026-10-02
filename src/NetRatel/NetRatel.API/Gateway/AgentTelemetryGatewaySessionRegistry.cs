using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using NetRatel.API.Realtime;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Services;
using NetRatel.API.Services;
using Google.Protobuf;

namespace NetRatel.API.Gateway;

public interface IAgentTelemetryGatewaySessionRegistry
{
    AgentTelemetryGatewaySessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion);
    AgentTelemetryGatewaySessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion, bool provisional) =>
        Register(client, connectionId, connectionEpoch, supportsDynamicSampling, agentVersion);
    AgentTelemetryGatewaySessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion, bool provisional, bool supportsServices) =>
        Register(client, connectionId, connectionEpoch, supportsDynamicSampling, agentVersion, provisional);
    AgentTelemetryGatewaySessionStatus GetStatus(ClientKey client);
    void PublishPolicy(ClientKey client, TelemetrySamplingPolicyState policy);
    bool TryPublishServicesPolicy(ClientKey client, ClientServiceWatchPolicyDto policy) => false;
    bool TryPublishServicesPolicy(ClientKey client, ClientServiceWatchPolicyDto policy, Guid expectedRegistrationId) =>
        GetStatus(client).RegistrationId == expectedRegistrationId && TryPublishServicesPolicy(client, policy);
}

public sealed record AgentTelemetryGatewaySessionStatus(
    bool Connected,
    bool SupportsDynamicSampling,
    string? AgentVersion,
    long? ConnectionEpoch)
{
    public bool SupportsServices { get; init; }
    public Guid? ConnectionId { get; init; }
    public Guid? RegistrationId { get; init; }
}

public sealed class AgentTelemetryGatewaySessionRegistry : IAgentTelemetryGatewaySessionRegistry
{
    private readonly ConcurrentDictionary<ClientKey, Session> _sessions = [];
    private readonly object _registrationGate = new();
    private readonly ITelemetryInteractiveDemandRegistry _demand;
    private readonly IGatewayTelemetryLiveRegistry _live;
    private readonly TimeProvider _timeProvider;

    public AgentTelemetryGatewaySessionRegistry(ITelemetryInteractiveDemandRegistry demand, IGatewayTelemetryLiveRegistry live, TimeProvider? timeProvider = null)
    {
        _demand = demand;
        _live = live;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _demand.PolicyChanged += PublishPolicy;
    }

    public AgentTelemetryGatewaySessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion) =>
        Register(client, connectionId, connectionEpoch, supportsDynamicSampling, agentVersion, provisional: false);

    public AgentTelemetryGatewaySessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion, bool provisional)
        => Register(client, connectionId, connectionEpoch, supportsDynamicSampling, agentVersion, provisional, supportsServices: false);

    public AgentTelemetryGatewaySessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion, bool provisional, bool supportsServices)
    {
        var replacement = new Session(client, connectionId, connectionEpoch, supportsDynamicSampling, agentVersion, supportsServices);
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
                {
                    SupportsServices = session.SupportsServices, ConnectionId = session.ConnectionId, RegistrationId = session.RegistrationId
                }
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

    public bool TryPublishServicesPolicy(ClientKey client, ClientServiceWatchPolicyDto policy)
        => TryPublishServicesPolicy(client, policy, expectedRegistrationId: null);

    public bool TryPublishServicesPolicy(ClientKey client, ClientServiceWatchPolicyDto policy, Guid expectedRegistrationId)
        => TryPublishServicesPolicy(client, policy, (Guid?)expectedRegistrationId);

    private bool TryPublishServicesPolicy(ClientKey client, ClientServiceWatchPolicyDto policy, Guid? expectedRegistrationId)
    {
        if (!ClientServicesCoordinator.IsValidPolicy(policy, _timeProvider.GetUtcNow())) return false;
        lock (_registrationGate)
        {
            return _sessions.TryGetValue(client, out var session) && session.Active &&
                (expectedRegistrationId is null || session.RegistrationId == expectedRegistrationId) &&
                session.PublishServicesPolicy(policy);
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

    internal sealed class Session(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion, bool supportsServices)
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
        private ClientServiceWatchPolicyDto? _latestServicesPolicy;
        private ulong _lastServicesRevision;
        private long _lastPolicyRevision = -1;
        private int _completed;

        public Guid RegistrationId { get; } = Guid.NewGuid();
        public bool Active { get; set; }
        public ClientKey Client { get; } = client;
        public Guid ConnectionId { get; } = connectionId;
        public ulong ConnectionEpoch { get; } = connectionEpoch;
        public bool SupportsDynamicSampling { get; } = supportsDynamicSampling;
        public bool SupportsServices { get; } = supportsServices;
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

        public bool PublishServicesPolicy(ClientServiceWatchPolicyDto policy)
        {
            if (!SupportsServices || Volatile.Read(ref _completed) != 0) return false;
            if (ToFrame(policy).CalculateSize() > ClientServicesLimits.MaximumChunkPayloadBytes) return false;
            lock (_policyGate)
            {
                if (policy.Revision < _lastServicesRevision || Volatile.Read(ref _completed) != 0) return false;
                // A cadence renewal cannot discard a queued explicit refresh.
                _latestServicesPolicy = policy with
                {
                    ServiceNames = policy.ServiceNames.ToArray(),
                    RefreshRequestId = policy.RefreshRequestId ?? _latestServicesPolicy?.RefreshRequestId
                };
                _lastServicesRevision = policy.Revision;
            }
            Pulse();
            return true;
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
                ClientServiceWatchPolicyDto? servicesPolicy;
                lock (_policyGate)
                {
                    policy = _latestPolicy;
                    _latestPolicy = null;
                    servicesPolicy = _latestServicesPolicy;
                    _latestServicesPolicy = null;
                }
                if (policy is not null)
                {
                    yield return ToFrame(policy);
                }
                if (servicesPolicy is not null) yield return ToFrame(servicesPolicy);
                if (policy is not null || servicesPolicy is not null) continue;

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
                    servicesPolicy = _latestServicesPolicy;
                    _latestServicesPolicy = null;
                }
                if (policy is not null) yield return ToFrame(policy);
                if (servicesPolicy is not null) yield return ToFrame(servicesPolicy);
                yield break;
            }
        }

        private void Pulse() => _wakeups.Writer.TryWrite(0);

        private GatewayTelemetryFrame ToFrame(ClientServiceWatchPolicyDto policy)
        {
            var wire = new ServiceWatchPolicy
            {
                Revision = policy.Revision,
                WatchIntervalSeconds = policy.WatchIntervalSeconds,
                InventoryIntervalSeconds = policy.InventoryIntervalSeconds,
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(policy.ExpiresAtUtc),
                RefreshRequestId = policy.RefreshRequestId?.ToString("D") ?? ""
            };
            wire.ServiceNames.Add(policy.ServiceNames);
            return new()
            {
                ProtocolVersion = "1.0", TenantId = Client.TenantId, ClientId = Client.AgentId.ToString("D"),
                ConnectionEpoch = ConnectionEpoch, ConnectionId = ConnectionId.ToString("D"), Sequence = 0,
                ServiceWatchPolicy = wire
            };
        }

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
    public bool SupportsServices => _session.SupportsServices;
    public CancellationToken CompletionToken => _session.CompletionToken;
    public ValueTask EnqueueReliableAsync(GatewayTelemetryFrame frame, CancellationToken cancellationToken) => _session.EnqueueReliableAsync(frame, cancellationToken);
    public IAsyncEnumerable<GatewayTelemetryFrame> ReadOutboundAsync(CancellationToken cancellationToken) => _session.ReadOutboundAsync(cancellationToken);
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _unregister();
        return ValueTask.CompletedTask;
    }
}

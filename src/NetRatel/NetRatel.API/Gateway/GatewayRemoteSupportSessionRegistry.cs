using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Observability;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.API.Gateway;

/// <summary>
/// DEV-canary remote-support signalling broker. It relays only bounded
/// signalling/control envelopes; WebRTC media remains browser-to-agent and is
/// never sent through this broker, actor state, or logs.
/// </summary>
public interface IGatewayRemoteSupportSessionRegistry
{
    AgentRemoteSupportGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch);

    Task<GatewayRemoteSupportSession> OpenAsync(ClientKey client, OpenRemoteSupportRequest request, CancellationToken cancellationToken);

    Task SendBrowserSignalAsync(string sessionId, RemoteSupportSignalRequest request, CancellationToken cancellationToken);

    Task CloseAsync(string sessionId, string? reason, CancellationToken cancellationToken);

    GatewayRemoteSupportSession? Get(string sessionId);

    GatewayRemoteSupportSignalSubscription Subscribe(string sessionId);

    bool TryReceiveAgentSignal(ClientKey client, RemoteSupportSignal signal);

    bool TryReceiveAgentClose(ClientKey client, RemoteSupportSessionClosed closed);
}

public sealed record GatewayRemoteSupportSession(
    string SessionId,
    int TenantId,
    Guid AgentId,
    string State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Authority,
    string? CloseReason);

public sealed class GatewayRemoteSupportSessionUnavailableException(ClientKey client)
    : InvalidOperationException($"No active remote-support gateway session exists for tenant {client.TenantId}, agent {client.AgentId:D}.");

public sealed class GatewayRemoteSupportSessionRegistry(
    IClientPresenceRouter presenceRouter,
    TimeProvider timeProvider,
    IHostEnvironment? environment = null)
    : IGatewayRemoteSupportSessionRegistry
{
    private const string Feature = "remote-support";
    private const string Authority = "akka";
    private readonly ConcurrentDictionary<ClientKey, AgentSupportTransportSession> _agentSessions = new();
    private readonly ConcurrentDictionary<string, SupportSession> _sessions = new(StringComparer.Ordinal);

    private string EnvironmentName => environment?.EnvironmentName ?? Environments.Development;

    public AgentRemoteSupportGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch)
    {
        var session = new AgentSupportTransportSession(client, connectionId, connectionEpoch, presenceRouter);
        var replaced = _agentSessions.AddOrUpdate(client, session, (_, existing) =>
        {
            existing.Complete();
            return session;
        });
        if (!ReferenceEquals(replaced, session))
        {
            throw new InvalidOperationException("The remote-support gateway session could not be registered.");
        }

        ResumeSessions(client, session);

        return new AgentRemoteSupportGatewayRegistration(session.Reader, () =>
        {
            if (((ICollection<KeyValuePair<ClientKey, AgentSupportTransportSession>>)_agentSessions)
                .Remove(new KeyValuePair<ClientKey, AgentSupportTransportSession>(client, session)))
            {
                session.Complete();
                foreach (var support in _sessions.Values.Where(value => value.Client == client))
                {
                    support.MarkReconnecting(timeProvider.GetUtcNow());
                }
            }
        });
    }

    public async Task<GatewayRemoteSupportSession> OpenAsync(ClientKey client, OpenRemoteSupportRequest request, CancellationToken cancellationToken)
    {
        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, fallbackUsed: false, EnvironmentName);
        SupportSession? openedSession = null;
        try
        {
            var transport = GetAgentSession(client);
            await transport.RequirePresenceLeaseAsync(cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var session = new SupportSession(Guid.NewGuid().ToString("N"), client, SerializeRequest(request), now);
            openedSession = session;
            if (!_sessions.TryAdd(session.SessionId, session))
            {
                throw new InvalidOperationException("The generated remote-support session ID was already in use.");
            }

            await transport.SendSignalAsync(session.SessionId, "open", session.OpenRequestPayload, session.NextBrowserSequence(), cancellationToken).ConfigureAwait(false);
            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, fallbackUsed: false, EnvironmentName);
            UpdateAuthoritySessionGauge();
            return session.Snapshot();
        }
        catch
        {
            if (openedSession is not null)
            {
                _sessions.TryRemove(openedSession.SessionId, out _);
                UpdateAuthoritySessionGauge();
            }

            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, EnvironmentName);
            throw;
        }
    }

    public async Task SendBrowserSignalAsync(string sessionId, RemoteSupportSignalRequest request, CancellationToken cancellationToken)
    {
        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, fallbackUsed: false, EnvironmentName);
        try
        {
            var session = GetRequiredSession(sessionId);
            EnsureOpen(session);
            ValidateSignal(request.SignalType, request.PayloadJson);
            var transport = GetAgentSession(session.Client);
            await transport.RequirePresenceLeaseAsync(cancellationToken).ConfigureAwait(false);
            await transport.SendSignalAsync(session.SessionId, request.SignalType, request.PayloadJson, session.NextBrowserSequence(), cancellationToken).ConfigureAwait(false);
            session.MarkBrowserSignalling(timeProvider.GetUtcNow());
            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, fallbackUsed: false, EnvironmentName);
        }
        catch
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, EnvironmentName);
            throw;
        }
    }

    public async Task CloseAsync(string sessionId, string? reason, CancellationToken cancellationToken)
    {
        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, fallbackUsed: false, EnvironmentName);
        try
        {
            var session = GetRequiredSession(sessionId);
            if (session.IsClosed)
            {
                return;
            }

            var closeReason = string.IsNullOrWhiteSpace(reason) ? "operator_cancelled" : reason.Trim()[..Math.Min(reason.Trim().Length, 128)];
            var transport = GetAgentSession(session.Client);
            await transport.SendCloseAsync(session.SessionId, closeReason, cancellationToken).ConfigureAwait(false);
            var completed = session.Close(closeReason, timeProvider.GetUtcNow());
            if (completed && session.IsCompleted)
            {
                NetRatelAkkaTelemetry.RemoteSupportAuthoritySessionCompleted(Authority, fallbackUsed: false, EnvironmentName);
            }

            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, fallbackUsed: false, EnvironmentName);
            UpdateAuthoritySessionGauge();
        }
        catch
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, EnvironmentName);
            throw;
        }
    }

    public GatewayRemoteSupportSession? Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session.Snapshot() : null;

    public GatewayRemoteSupportSignalSubscription Subscribe(string sessionId) => GetRequiredSession(sessionId).Subscribe();

    public bool TryReceiveAgentSignal(ClientKey client, RemoteSupportSignal signal)
    {
        if (!_sessions.TryGetValue(signal.SessionId, out var session) || session.Client != client ||
            !Guid.TryParse(signal.MessageId, out var messageId) || messageId == Guid.Empty || signal.SessionSequence == 0 ||
            !ValidateSignalOrFalse(signal.SignalType, signal.Payload) || !session.TryAcceptAgentSequence(signal.SessionSequence))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        session.ApplyAgentSignal(signal.SignalType, now);
        session.Publish(new GatewayRemoteSupportSignalDto(
            session.SessionId,
            signal.SignalType,
            signal.Payload.ToStringUtf8(),
            signal.SessionSequence,
            "agent",
            now));
        if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Reject, StringComparison.OrdinalIgnoreCase))
        {
            session.Reject("agent_rejected", now);
            UpdateAuthoritySessionGauge();
            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, fallbackUsed: false, EnvironmentName);
        }
        else if (string.Equals(signal.SignalType, RemoteSupportSignalTypes.Error, StringComparison.OrdinalIgnoreCase))
        {
            session.Fail("agent_rejected_or_failed", now);
            UpdateAuthoritySessionGauge();
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, EnvironmentName);
        }
        else
        {
            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, fallbackUsed: false, EnvironmentName);
        }
        return true;
    }

    public bool TryReceiveAgentClose(ClientKey client, RemoteSupportSessionClosed closed)
    {
        if (!_sessions.TryGetValue(closed.SessionId, out var session) || session.Client != client)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var completed = session.Close(string.IsNullOrWhiteSpace(closed.Reason) ? "agent_completed" : closed.Reason, now);
        if (completed && session.IsCompleted)
        {
            NetRatelAkkaTelemetry.RemoteSupportAuthoritySessionCompleted(Authority, fallbackUsed: false, EnvironmentName);
        }

        NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, fallbackUsed: false, EnvironmentName);
        UpdateAuthoritySessionGauge();
        return true;
    }

    private void ResumeSessions(ClientKey client, AgentSupportTransportSession transport)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var support in _sessions.Values.Where(value => value.Client == client && !value.IsClosed))
        {
            // The gateway stream is fenced by the presence connection epoch. A
            // replacement stream starts its per-session sequence at one, so the
            // previous stream's sequence fence must not reject its first Ready.
            support.ResetAgentSequenceForReconnect(now);
            support.MarkReconnecting(now);
            if (transport.TrySendSignal(support.SessionId, "open", support.OpenRequestPayload, support.NextBrowserSequence()))
            {
                support.PublishGateway(RemoteSupportSignalTypes.Reconnect, "{}", now);
                continue;
            }

            support.Fail("agent_gateway_resume_overflow", now);
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, EnvironmentName);
        }

        UpdateAuthoritySessionGauge();
    }

    private void UpdateAuthoritySessionGauge() =>
        NetRatelAkkaTelemetry.SetRemoteSupportAuthoritySessionsActive(_sessions.Values.Count(session => !session.IsClosed));

    private AgentSupportTransportSession GetAgentSession(ClientKey client) =>
        _agentSessions.TryGetValue(client, out var session)
            ? session
            : throw new GatewayRemoteSupportSessionUnavailableException(client);

    private SupportSession GetRequiredSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new KeyNotFoundException("The remote-support session was not found.");

    private static void EnsureOpen(SupportSession session)
    {
        if (session.IsClosed)
        {
            throw new InvalidOperationException("The remote-support session is closed.");
        }
    }

    private static void ValidateSignal(string? signalType, string? payloadJson)
    {
        if (!ValidateSignalOrFalse(signalType, ByteString.CopyFromUtf8(payloadJson ?? string.Empty)))
        {
            throw new ArgumentException("The remote-support signal is invalid or exceeds its 32 KiB limit.");
        }
    }

    private static bool ValidateSignalOrFalse(string? signalType, ByteString payload) =>
        !string.IsNullOrWhiteSpace(signalType) && signalType.Length <= 32 && payload.Length <= 32 * 1024;

    private static string SerializeRequest(OpenRemoteSupportRequest request) =>
        System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    private sealed class SupportSession(
        string sessionId,
        ClientKey client,
        string openRequestPayload,
        DateTimeOffset createdAtUtc)
    {
        private const int SignalCapacity = 64;
        private readonly object _sync = new();
        private readonly Dictionary<Guid, Channel<GatewayRemoteSupportSignalDto>> _subscribers = [];
        private readonly Queue<GatewayRemoteSupportSignalDto> _recentSignals = new(SignalCapacity);
        private ulong _browserSequence;
        private ulong _lastAgentSequence;
        private DateTimeOffset _updatedAtUtc = createdAtUtc;
        private string? _closeReason;
        private string _state = "requested";

        public string SessionId { get; } = sessionId;
        public ClientKey Client { get; } = client;
        public string OpenRequestPayload { get; } = openRequestPayload;
        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
        public DateTimeOffset UpdatedAtUtc { get { lock (_sync) return _updatedAtUtc; } }
        public string? CloseReason { get { lock (_sync) return _closeReason; } }
        public bool IsClosed { get { lock (_sync) return _closeReason is not null; } }
        public bool IsCompleted { get { lock (_sync) return _state == "completed"; } }
        public ulong NextBrowserSequence()
        {
            lock (_sync)
            {
                return checked(++_browserSequence);
            }
        }

        public void MarkBrowserSignalling(DateTimeOffset now)
        {
            lock (_sync)
            {
                if (_closeReason is null && _state is "requested" or "accepted" or "reconnecting")
                {
                    _state = "started";
                }

                _updatedAtUtc = now;
            }
        }

        public void MarkReconnecting(DateTimeOffset now)
        {
            lock (_sync)
            {
                if (_closeReason is null)
                {
                    _state = "reconnecting";
                    _updatedAtUtc = now;
                }
            }
        }

        public void ResetAgentSequenceForReconnect(DateTimeOffset now)
        {
            lock (_sync)
            {
                if (_closeReason is null)
                {
                    _lastAgentSequence = 0;
                    _updatedAtUtc = now;
                }
            }
        }

        public void ApplyAgentSignal(string signalType, DateTimeOffset now)
        {
            lock (_sync)
            {
                if (_closeReason is not null)
                {
                    return;
                }

                _state = signalType.Trim().ToLowerInvariant() switch
                {
                    RemoteSupportSignalTypes.Ready => "accepted",
                    RemoteSupportSignalTypes.Answer or RemoteSupportSignalTypes.Ice => "started",
                    _ => _state
                };
                _updatedAtUtc = now;
            }
        }

        public bool TryAcceptAgentSequence(ulong sequence)
        {
            lock (_sync)
            {
                if (_closeReason is not null || sequence <= _lastAgentSequence)
                {
                    return false;
                }

                _lastAgentSequence = sequence;
                return true;
            }
        }

        public void Publish(GatewayRemoteSupportSignalDto signal)
        {
            lock (_sync)
            {
                if (_closeReason is not null)
                {
                    return;
                }

                _updatedAtUtc = signal.ReceivedAtUtc;
                if (_recentSignals.Count == SignalCapacity)
                {
                    _recentSignals.Dequeue();
                }

                _recentSignals.Enqueue(signal);
                // Signalling is ordered control data: a slow browser must be
                // disconnected rather than silently losing an SDP/ICE envelope.
                foreach (var (subscriberId, subscriber) in _subscribers.ToArray())
                {
                    if (subscriber.Writer.TryWrite(signal))
                    {
                        continue;
                    }

                    _subscribers.Remove(subscriberId);
                    subscriber.Writer.TryComplete(new InvalidOperationException("The remote-support signal consumer was too slow."));
                }
            }
        }

        public void PublishGateway(string signalType, string payloadJson, DateTimeOffset now) =>
            Publish(new GatewayRemoteSupportSignalDto(SessionId, signalType, payloadJson, 0, "gateway", now));

        public GatewayRemoteSupportSignalSubscription Subscribe()
        {
            lock (_sync)
            {
                if (_closeReason is not null)
                {
                    var completed = Channel.CreateBounded<GatewayRemoteSupportSignalDto>(1);
                    completed.Writer.TryComplete();
                    return new GatewayRemoteSupportSignalSubscription(completed.Reader, static () => { });
                }

                var subscriber = Channel.CreateBounded<GatewayRemoteSupportSignalDto>(new BoundedChannelOptions(SignalCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
                foreach (var signal in _recentSignals)
                {
                    if (!subscriber.Writer.TryWrite(signal))
                    {
                        throw new InvalidOperationException("The remote-support signal history exceeded the subscriber capacity.");
                    }
                }

                var subscriberId = Guid.NewGuid();
                _subscribers.Add(subscriberId, subscriber);
                return new GatewayRemoteSupportSignalSubscription(subscriber.Reader, () =>
                {
                    lock (_sync)
                    {
                        if (_subscribers.Remove(subscriberId, out var removed))
                        {
                            removed.Writer.TryComplete();
                        }
                    }
                });
            }
        }

        public bool Close(string reason, DateTimeOffset now)
        {
            lock (_sync)
            {
                if (_closeReason is not null)
                {
                    return false;
                }

                _closeReason = reason;
                _state = IsCompletionReason(reason) ? "completed" : "cancelled";
                _updatedAtUtc = now;
                foreach (var (_, subscriber) in _subscribers)
                {
                    subscriber.Writer.TryComplete();
                }
                _subscribers.Clear();
                return true;
            }
        }

        public void Fail(string reason, DateTimeOffset now)
        {
            lock (_sync)
            {
                if (_closeReason is not null)
                {
                    return;
                }

                _closeReason = reason;
                _state = "failed";
                _updatedAtUtc = now;
                foreach (var (_, subscriber) in _subscribers)
                {
                    subscriber.Writer.TryComplete();
                }

                _subscribers.Clear();
            }
        }

        public void Reject(string reason, DateTimeOffset now)
        {
            lock (_sync)
            {
                if (_closeReason is not null)
                {
                    return;
                }

                _closeReason = reason;
                _state = "rejected";
                _updatedAtUtc = now;
                foreach (var (_, subscriber) in _subscribers)
                {
                    subscriber.Writer.TryComplete();
                }

                _subscribers.Clear();
            }
        }

        public GatewayRemoteSupportSession Snapshot()
        {
            lock (_sync)
            {
                return new GatewayRemoteSupportSession(SessionId, Client.TenantId, Client.AgentId,
                    _state, CreatedAtUtc, _updatedAtUtc, "akka", _closeReason);
            }
        }

        private static bool IsCompletionReason(string reason) =>
            reason.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            reason.Equals("agent_completed", StringComparison.OrdinalIgnoreCase) ||
            reason.Equals("operator_finished", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class GatewayRemoteSupportSignalSubscription(ChannelReader<GatewayRemoteSupportSignalDto> reader, Action dispose) : IDisposable
{
    private int _disposed;

    public ChannelReader<GatewayRemoteSupportSignalDto> Reader { get; } = reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            dispose();
        }
    }
}

public sealed class AgentRemoteSupportGatewayRegistration(ChannelReader<GatewayRemoteSupportFrame> reader, Action unregister) : IDisposable
{
    private int _disposed;
    public ChannelReader<GatewayRemoteSupportFrame> Reader { get; } = reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            unregister();
        }
    }
}

internal sealed class AgentSupportTransportSession(ClientKey client, Guid connectionId, ulong connectionEpoch, IClientPresenceRouter presenceRouter)
{
    private readonly Channel<GatewayRemoteSupportFrame> _outbound = Channel.CreateBounded<GatewayRemoteSupportFrame>(new BoundedChannelOptions(64)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private long _sequence;

    public ChannelReader<GatewayRemoteSupportFrame> Reader => _outbound.Reader;

    public async Task SendSignalAsync(string sessionId, string signalType, string payloadJson, ulong sessionSequence, CancellationToken cancellationToken) =>
        await WriteAsync(new GatewayRemoteSupportFrame
        {
            Signal = new RemoteSupportSignal
            {
                SessionId = sessionId,
                MessageId = Guid.NewGuid().ToString("N"),
                SignalType = signalType,
                SessionSequence = sessionSequence,
                Payload = ByteString.CopyFromUtf8(payloadJson)
            }
        }, cancellationToken).ConfigureAwait(false);

    public bool TrySendSignal(string sessionId, string signalType, string payloadJson, ulong sessionSequence) =>
        TryWrite(new GatewayRemoteSupportFrame
        {
            Signal = new RemoteSupportSignal
            {
                SessionId = sessionId,
                MessageId = Guid.NewGuid().ToString("N"),
                SignalType = signalType,
                SessionSequence = sessionSequence,
                Payload = ByteString.CopyFromUtf8(payloadJson)
            }
        });

    public async Task SendCloseAsync(string sessionId, string reason, CancellationToken cancellationToken) =>
        await WriteAsync(new GatewayRemoteSupportFrame
        {
            Closed = new RemoteSupportSessionClosed { SessionId = sessionId, Reason = reason }
        }, cancellationToken).ConfigureAwait(false);

    public async Task RequirePresenceLeaseAsync(CancellationToken cancellationToken)
    {
        var presence = await presenceRouter.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (presence.Status != ShadowPresenceStatus.Online || presence.ConnectionId != connectionId || presence.ConnectionEpoch != checked((long)connectionEpoch))
        {
            throw new GatewayRemoteSupportSessionUnavailableException(client);
        }
    }

    public void Complete() => _outbound.Writer.TryComplete();

    private async Task WriteAsync(GatewayRemoteSupportFrame frame, CancellationToken cancellationToken)
    {
        PopulateEnvelope(frame);
        await _outbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private bool TryWrite(GatewayRemoteSupportFrame frame)
    {
        PopulateEnvelope(frame);
        return _outbound.Writer.TryWrite(frame);
    }

    private void PopulateEnvelope(GatewayRemoteSupportFrame frame)
    {
        frame.ProtocolVersion = "1.0";
        frame.TenantId = client.TenantId;
        frame.ClientId = client.AgentId.ToString("D");
        frame.ConnectionEpoch = connectionEpoch;
        frame.ConnectionId = connectionId.ToString("D");
        frame.Sequence = checked((ulong)Interlocked.Increment(ref _sequence));
    }
}

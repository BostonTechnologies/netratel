using Akka.Actor;
using Akka.Event;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Observability;
using NetRatel.Application.Presence;

namespace NetRatel.Akka.Presence;

/// <summary>
/// Owns gateway presence state for one authenticated client.
/// </summary>
public sealed class PresenceActor : ReceiveActor, IWithTimers
{
    private const string ExpiryTimerKey = "gateway-presence-expiry";
    private const string AuthenticationExpiryTimerKey = "gateway-authentication-expiry";
    private readonly ClientKey _client;
    private readonly NetRatelAkkaOptions _options;
    private readonly IActorRef _presenceReadModel;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    private ClientPresenceStatus _status = ClientPresenceStatus.Unknown;
    private long _lastIssuedEpoch;
    private long? _activeEpoch;
    private Guid? _activeConnectionId;
    private ulong _lastAcceptedSequence;
    private DateTimeOffset? _lastReceivedAtUtc;
    private double? _latencyMilliseconds;
    private DateTimeOffset? _latencyMeasuredAtUtc;
    private string? _agentVersion;
    private IReadOnlyList<string> _capabilities = Array.Empty<string>();
    private string? _legacySpacetimeIdentity;
    private DateTimeOffset? _authenticationExpiresAtUtc;

    public PresenceActor(
        ClientKey client,
        NetRatelAkkaOptions options,
        IActorRef presenceReadModel,
        TimeProvider? timeProvider = null)
    {
        if (!client.IsValid)
        {
            throw new ArgumentException("A positive tenant ID and non-empty agent ID are required.", nameof(client));
        }

        _client = client;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _presenceReadModel = presenceReadModel ?? throw new ArgumentNullException(nameof(presenceReadModel));
        _timeProvider = timeProvider ?? TimeProvider.System;

        Receive<StartGatewayPresenceSession>(HandleStartSession);
        Receive<RecordGatewayHeartbeat>(HandleHeartbeat);
        Receive<EndGatewayPresenceSession>(HandleEndSession);
        Receive<GetClientPresence>(_ => Sender.Tell(CreateSnapshot()));
        Receive<PresenceDeadlineElapsed>(HandleDeadlineElapsed);
        Receive<AuthenticationDeadlineElapsed>(HandleAuthenticationDeadlineElapsed);
    }

    public ITimerScheduler Timers { get; set; } = null!;

    public static Props Props(
        ClientKey client,
        NetRatelAkkaOptions options,
        IActorRef presenceReadModel,
        TimeProvider? timeProvider = null) =>
        global::Akka.Actor.Props.Create(() => new PresenceActor(client, options, presenceReadModel, timeProvider));

    private void HandleStartSession(StartGatewayPresenceSession message)
    {
        EnsureClient(message.Client);

        if (message.AuthenticationExpiresAtUtc is { } requestedExpiry &&
            (requestedExpiry <= _timeProvider.GetUtcNow() || requestedExpiry <= message.ReceivedAtUtc))
        {
            Sender.Tell(new GatewayPresenceSessionStarted(_client, message.ConnectionId, _activeEpoch ?? 0,
                PresenceMessageDisposition.AuthenticationExpired, message.ReceivedAtUtc));
            return;
        }

        if (_activeConnectionId == message.ConnectionId && _activeEpoch.HasValue)
        {
            Sender.Tell(new GatewayPresenceSessionStarted(
                _client,
                message.ConnectionId,
                _activeEpoch.Value,
                PresenceMessageDisposition.Duplicate,
                _lastReceivedAtUtc ?? message.ReceivedAtUtc));
            return;
        }

        _lastIssuedEpoch = checked(_lastIssuedEpoch + 1);
        _activeEpoch = _lastIssuedEpoch;
        _activeConnectionId = message.ConnectionId;
        _lastAcceptedSequence = 0;
        _lastReceivedAtUtc = message.ReceivedAtUtc;
        _authenticationExpiresAtUtc = message.AuthenticationExpiresAtUtc;
        ClearLatency();
        _agentVersion = string.IsNullOrWhiteSpace(message.AgentVersion) ? null : message.AgentVersion;
        _capabilities = message.Capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _legacySpacetimeIdentity = string.IsNullOrWhiteSpace(message.LegacySpacetimeIdentity)
            ? null
            : message.LegacySpacetimeIdentity;
        var wasOnline = _status == ClientPresenceStatus.Online;
        _status = ClientPresenceStatus.Online;
        if (!wasOnline)
        {
            NetRatelAkkaTelemetry.PresenceClientConnected();
        }
        ScheduleExpiry(_lastIssuedEpoch, message.ConnectionId);
        ScheduleAuthenticationExpiry(_lastIssuedEpoch, message.ConnectionId);
        PublishTransition(message.ReceivedAtUtc, "connected");
        PublishReadModelSnapshot();

        _log.Info(
            "Gateway presence connected. authority={0}, client={1}, epoch={2}, connectionId={3}",
            "akka",
            _client,
            _lastIssuedEpoch,
            message.ConnectionId);

        Sender.Tell(new GatewayPresenceSessionStarted(
            _client,
            message.ConnectionId,
            _lastIssuedEpoch,
            PresenceMessageDisposition.Accepted,
            message.ReceivedAtUtc));
    }

    private void HandleHeartbeat(RecordGatewayHeartbeat message)
    {
        EnsureClient(message.Client);
        var disposition = ValidateActiveSession(message.ConnectionId, message.ConnectionEpoch);
        if (disposition != PresenceMessageDisposition.Accepted)
        {
            Sender.Tell(CreateResult(disposition));
            return;
        }

        // Once the authoritative timer or explicit close retires this owner,
        // a delayed frame cannot revive it. A new authenticated admission is required.
        if (_status == ClientPresenceStatus.Offline)
        {
            Sender.Tell(CreateResult(PresenceMessageDisposition.NoActiveSession));
            return;
        }

        if (_authenticationExpiresAtUtc is { } currentExpiry &&
            (currentExpiry <= _timeProvider.GetUtcNow() || currentExpiry <= message.ReceivedAtUtc))
        {
            RetireAuthentication(currentExpiry);
            Sender.Tell(CreateResult(PresenceMessageDisposition.AuthenticationExpired));
            return;
        }

        if (message.Sequence == _lastAcceptedSequence)
        {
            Sender.Tell(CreateResult(PresenceMessageDisposition.Duplicate));
            return;
        }

        if (message.Sequence < _lastAcceptedSequence)
        {
            Sender.Tell(CreateResult(PresenceMessageDisposition.StaleSequence));
            return;
        }

        if (message.RenewedAuthenticationExpiresAtUtc is { } renewedExpiry)
        {
            if (_authenticationExpiresAtUtc is not { } previousExpiry || renewedExpiry <= previousExpiry)
            {
                Sender.Tell(CreateResult(PresenceMessageDisposition.InvalidAuthenticationRenewal));
                return;
            }
            // Only the synchronous, exact-owner accepted sequence can extend
            // authentication. The serializable message carries validated
            // expiry, never a credential or a process-local cancellation token.
            _authenticationExpiresAtUtc = renewedExpiry;
            ScheduleAuthenticationExpiry(message.ConnectionEpoch, message.ConnectionId);
        }

        var wasOnline = _status == ClientPresenceStatus.Online;
        _lastAcceptedSequence = message.Sequence;
        _lastReceivedAtUtc = message.ReceivedAtUtc;
        if (message.HeartbeatRoundTripMilliseconds is { } latency &&
            double.IsFinite(latency) && latency >= 0 && latency <= _options.HeartbeatTimeout.TotalMilliseconds &&
            message.LatencyMeasuredAtUtc is { } measuredAt && measuredAt <= message.ReceivedAtUtc &&
            message.ReceivedAtUtc - measuredAt <= _options.HeartbeatTimeout)
        {
            _latencyMilliseconds = latency;
            _latencyMeasuredAtUtc = measuredAt;
        }
        _status = ClientPresenceStatus.Online;
        ScheduleExpiry(message.ConnectionEpoch, message.ConnectionId);
        if (!wasOnline)
        {
            PublishTransition(message.ReceivedAtUtc, "heartbeat");
        }
        PublishReadModelSnapshot();

        Sender.Tell(CreateResult(PresenceMessageDisposition.Accepted));
    }

    private void HandleEndSession(EndGatewayPresenceSession message)
    {
        EnsureClient(message.Client);
        var disposition = ValidateActiveSession(message.ConnectionId, message.ConnectionEpoch);
        if (disposition != PresenceMessageDisposition.Accepted)
        {
            Sender.Tell(CreateResult(disposition));
            return;
        }

        var wasOffline = _status == ClientPresenceStatus.Offline;
        Timers.Cancel(ExpiryTimerKey);
        Timers.Cancel(AuthenticationExpiryTimerKey);
        _status = ClientPresenceStatus.Offline;
        ClearLatency();
        _lastReceivedAtUtc = message.ReceivedAtUtc;
        if (!wasOffline)
        {
            NetRatelAkkaTelemetry.PresenceClientDisconnected();
            PublishTransition(message.ReceivedAtUtc, "disconnected");
        }
        PublishReadModelSnapshot();

        _log.Info(
            "Gateway presence disconnected. authority={0}, client={1}, epoch={2}, connectionId={3}, reason={4}",
            "akka",
            _client,
            message.ConnectionEpoch,
            message.ConnectionId,
            message.Reason);

        Sender.Tell(CreateResult(PresenceMessageDisposition.Accepted));
    }

    private void HandleDeadlineElapsed(PresenceDeadlineElapsed message)
    {
        if (_activeEpoch != message.ConnectionEpoch || _activeConnectionId != message.ConnectionId)
        {
            return;
        }

        if (_status == ClientPresenceStatus.Offline)
        {
            return;
        }

        _status = ClientPresenceStatus.Offline;
        Timers.Cancel(AuthenticationExpiryTimerKey);
        ClearLatency();
        NetRatelAkkaTelemetry.PresenceClientHeartbeatExpired();
        PublishTransition(
            (_lastReceivedAtUtc ?? DateTimeOffset.UtcNow) + _options.HeartbeatTimeout,
            "heartbeat-expired");
        PublishReadModelSnapshot();

        _log.Warning(
            "Gateway heartbeat expired. authority={0}, client={1}, epoch={2}, connectionId={3}",
            "akka",
            _client,
            message.ConnectionEpoch,
            message.ConnectionId);
    }

    private PresenceMessageDisposition ValidateActiveSession(Guid connectionId, long connectionEpoch)
    {
        if (!_activeEpoch.HasValue || !_activeConnectionId.HasValue)
        {
            return PresenceMessageDisposition.NoActiveSession;
        }

        if (connectionEpoch != _activeEpoch.Value)
        {
            return PresenceMessageDisposition.StaleConnectionEpoch;
        }

        return connectionId == _activeConnectionId.Value
            ? PresenceMessageDisposition.Accepted
            : PresenceMessageDisposition.ConnectionMismatch;
    }

    private void ScheduleExpiry(long connectionEpoch, Guid connectionId) =>
        Timers.StartSingleTimer(
            ExpiryTimerKey,
            new PresenceDeadlineElapsed(connectionEpoch, connectionId),
            _options.HeartbeatTimeout);

    private void ScheduleAuthenticationExpiry(long connectionEpoch, Guid connectionId)
    {
        Timers.Cancel(AuthenticationExpiryTimerKey);
        if (_authenticationExpiresAtUtc is { } expiry)
            Timers.StartSingleTimer(AuthenticationExpiryTimerKey,
                new AuthenticationDeadlineElapsed(connectionEpoch, connectionId, expiry),
                expiry - _timeProvider.GetUtcNow());
    }

    private void HandleAuthenticationDeadlineElapsed(AuthenticationDeadlineElapsed message)
    {
        if (_activeEpoch != message.ConnectionEpoch || _activeConnectionId != message.ConnectionId ||
            _authenticationExpiresAtUtc != message.ExpiresAtUtc || _status != ClientPresenceStatus.Online)
            return;
        if (_timeProvider.GetUtcNow() < message.ExpiresAtUtc)
        {
            ScheduleAuthenticationExpiry(message.ConnectionEpoch, message.ConnectionId);
            return;
        }
        RetireAuthentication(message.ExpiresAtUtc);
    }

    private void RetireAuthentication(DateTimeOffset expiry)
    {
        Timers.Cancel(ExpiryTimerKey);
        Timers.Cancel(AuthenticationExpiryTimerKey);
        _status = ClientPresenceStatus.Offline;
        ClearLatency();
        NetRatelAkkaTelemetry.PresenceClientDisconnected();
        PublishTransition(expiry, "authentication-expired");
        PublishReadModelSnapshot();
    }

    private PresenceMessageResult CreateResult(PresenceMessageDisposition disposition) =>
        new(_client, _activeEpoch, disposition, _lastAcceptedSequence);

    private ClientPresenceSnapshot CreateSnapshot() =>
        new(
            _client,
            _status,
            _activeEpoch,
            _activeConnectionId,
            _lastAcceptedSequence,
            _lastReceivedAtUtc,
            _agentVersion,
            _capabilities,
            _legacySpacetimeIdentity,
            "akka",
            IsAuthoritative: true,
            LatencyMilliseconds: _latencyMilliseconds,
            LatencyMeasuredAtUtc: _latencyMeasuredAtUtc,
            LatencyExpiresAtUtc: _latencyMeasuredAtUtc + _options.HeartbeatTimeout,
            AuthenticationExpiresAtUtc: _authenticationExpiresAtUtc);

    private void ClearLatency()
    {
        _latencyMilliseconds = null;
        _latencyMeasuredAtUtc = null;
    }

    private void PublishReadModelSnapshot() =>
        _presenceReadModel.Tell(new TrackClientPresenceSnapshot(CreateSnapshot()), Self);

    private void PublishTransition(DateTimeOffset changedAtUtc, string reason)
    {
        if (_activeEpoch.HasValue)
        {
            Context.System.EventStream.Publish(new ClientPresenceChanged(
                _client,
                _status,
                _activeEpoch.Value,
                changedAtUtc,
                reason,
                IsAuthoritative: true));
        }
    }

    private void EnsureClient(ClientKey client)
    {
        if (client != _client)
        {
            throw new InvalidOperationException($"Presence message for {client} reached actor for {_client}.");
        }
    }

    internal sealed record PresenceDeadlineElapsed(long ConnectionEpoch, Guid ConnectionId);
    internal sealed record AuthenticationDeadlineElapsed(long ConnectionEpoch, Guid ConnectionId, DateTimeOffset ExpiresAtUtc);
}

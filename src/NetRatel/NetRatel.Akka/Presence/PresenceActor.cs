using Akka.Actor;
using Akka.Event;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Observability;
using NetRatel.Application.Presence;

namespace NetRatel.Akka.Presence;

/// <summary>
/// Owns gateway presence state for one authenticated client. It remains a
/// shadow unless the DEV-only presence authority flag is explicitly enabled.
/// </summary>
public sealed class PresenceActor : ReceiveActor, IWithTimers
{
    private const string ExpiryTimerKey = "gateway-presence-expiry";
    private readonly ClientKey _client;
    private readonly NetRatelAkkaMigrationOptions _options;
    private readonly IActorRef _presenceReadModel;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    private ShadowPresenceStatus _status = ShadowPresenceStatus.Unknown;
    private long _lastIssuedEpoch;
    private long? _activeEpoch;
    private Guid? _activeConnectionId;
    private ulong _lastAcceptedSequence;
    private DateTimeOffset? _lastReceivedAtUtc;
    private string? _agentVersion;
    private IReadOnlyList<string> _capabilities = Array.Empty<string>();
    private string? _legacySpacetimeIdentity;

    public PresenceActor(
        ClientKey client,
        NetRatelAkkaMigrationOptions options,
        IActorRef presenceReadModel)
    {
        if (!client.IsValid)
        {
            throw new ArgumentException("A positive tenant ID and non-empty agent ID are required.", nameof(client));
        }

        _client = client;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _presenceReadModel = presenceReadModel ?? throw new ArgumentNullException(nameof(presenceReadModel));

        Receive<StartGatewayPresenceSession>(HandleStartSession);
        Receive<RecordGatewayHeartbeat>(HandleHeartbeat);
        Receive<EndGatewayPresenceSession>(HandleEndSession);
        Receive<GetClientPresence>(_ => Sender.Tell(CreateSnapshot()));
        Receive<PresenceDeadlineElapsed>(HandleDeadlineElapsed);
    }

    public ITimerScheduler Timers { get; set; } = null!;

    public static Props Props(
        ClientKey client,
        NetRatelAkkaMigrationOptions options,
        IActorRef presenceReadModel) =>
        global::Akka.Actor.Props.Create(() => new PresenceActor(client, options, presenceReadModel));

    private void HandleStartSession(StartGatewayPresenceSession message)
    {
        EnsureClient(message.Client);

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
        _agentVersion = string.IsNullOrWhiteSpace(message.AgentVersion) ? null : message.AgentVersion;
        _capabilities = message.Capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _legacySpacetimeIdentity = string.IsNullOrWhiteSpace(message.LegacySpacetimeIdentity)
            ? null
            : message.LegacySpacetimeIdentity;
        var wasOnline = _status == ShadowPresenceStatus.Online;
        _status = ShadowPresenceStatus.Online;
        if (!wasOnline)
        {
            NetRatelAkkaTelemetry.PresenceClientConnected();
        }
        ScheduleExpiry(_lastIssuedEpoch, message.ConnectionId);
        PublishTransition(message.ReceivedAtUtc, "connected");
        PublishReadModelSnapshot();

        _log.Info(
            "Gateway presence connected. authority={0}, client={1}, epoch={2}, connectionId={3}",
            _options.PresenceAuthority,
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

        var wasOnline = _status == ShadowPresenceStatus.Online;
        _lastAcceptedSequence = message.Sequence;
        _lastReceivedAtUtc = message.ReceivedAtUtc;
        _status = ShadowPresenceStatus.Online;
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

        var wasOffline = _status == ShadowPresenceStatus.Offline;
        Timers.Cancel(ExpiryTimerKey);
        _status = ShadowPresenceStatus.Offline;
        _lastReceivedAtUtc = message.ReceivedAtUtc;
        if (!wasOffline)
        {
            NetRatelAkkaTelemetry.PresenceClientDisconnected();
            PublishTransition(message.ReceivedAtUtc, "disconnected");
        }
        PublishReadModelSnapshot();

        _log.Info(
            "Gateway presence disconnected. authority={0}, client={1}, epoch={2}, connectionId={3}, reason={4}",
            _options.PresenceAuthority,
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

        if (_status == ShadowPresenceStatus.Offline)
        {
            return;
        }

        _status = ShadowPresenceStatus.Offline;
        NetRatelAkkaTelemetry.PresenceClientHeartbeatExpired();
        PublishTransition(
            (_lastReceivedAtUtc ?? DateTimeOffset.UtcNow) + _options.HeartbeatTimeout,
            "heartbeat-expired");
        PublishReadModelSnapshot();

        _log.Warning(
            "Gateway heartbeat expired. authority={0}, client={1}, epoch={2}, connectionId={3}",
            _options.PresenceAuthority,
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
            _options.PresenceAuthority,
            IsAuthoritative: _options.IsPresenceAuthorityActive);

    private void PublishReadModelSnapshot() =>
        _presenceReadModel.Tell(new TrackClientPresenceSnapshot(CreateSnapshot()), Self);

    private void PublishTransition(DateTimeOffset changedAtUtc, string reason)
    {
        if (_activeEpoch.HasValue)
        {
            Context.System.EventStream.Publish(new ShadowPresenceChanged(
                _client,
                _status,
                _activeEpoch.Value,
                changedAtUtc,
                reason,
                IsAuthoritative: _options.IsPresenceAuthorityActive));
        }
    }

    private void EnsureClient(ClientKey client)
    {
        if (client != _client)
        {
            throw new InvalidOperationException($"Presence message for {client} reached actor for {_client}.");
        }
    }

    private sealed record PresenceDeadlineElapsed(long ConnectionEpoch, Guid ConnectionId);
}

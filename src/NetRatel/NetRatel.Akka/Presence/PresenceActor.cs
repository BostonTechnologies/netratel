using Akka.Actor;
using Akka.Event;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Observability;
using NetRatel.Application.Presence;

namespace NetRatel.Akka.Presence;

/// <summary>
/// Owns gateway presence state for one authenticated client.
/// </summary>
public sealed partial class PresenceActor : ReceiveActor, IWithTimers
{
    private const string ExpiryTimerKey = "gateway-presence-expiry";
    private const string AuthenticationExpiryTimerKey = "gateway-authentication-expiry";
    private const string AdmissionPruneTimerKey = "gateway-admission-prune";
    private const int MaximumPendingAdmissions = 16;
    private const int MaximumCancelledAdmissions = 64;
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
    private readonly Dictionary<Guid, PendingAdmission> _pendingAdmissions = [];
    private readonly Dictionary<Guid, DateTimeOffset> _cancelledAdmissions = [];
    private DateTimeOffset? _admissionCancellationBarrierUntilUtc;

    public PresenceActor(
        ClientKey client,
        NetRatelAkkaOptions options,
        IActorRef presenceReadModel,
        TimeProvider? timeProvider = null,
        IClientConnectionEpochStore? ownership = null)
    {
        if (!client.IsValid)
        {
            throw new ArgumentException("A positive tenant ID and non-empty agent ID are required.", nameof(client));
        }

        _client = client;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _presenceReadModel = presenceReadModel ?? throw new ArgumentNullException(nameof(presenceReadModel));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownership = ownership;
        RegisterOwnershipHandlers();

        Receive<StartGatewayPresenceSession>(HandleStartSession);
        Receive<RecordGatewayHeartbeat>(HandleHeartbeat);
        Receive<EndGatewayPresenceSession>(HandleEndSession);
        Receive<GetClientPresence>(_ => Sender.Tell(CreateSnapshot()));
        Receive<PresenceDeadlineElapsed>(HandleDeadlineElapsed);
        Receive<AuthenticationDeadlineElapsed>(HandleAuthenticationDeadlineElapsed);
        Receive<PruneAdmissions>(_ => PrunePendingAdmissions());
    }

    public ITimerScheduler Timers { get; set; } = null!;

    public static Props Props(
        ClientKey client,
        NetRatelAkkaOptions options,
        IActorRef presenceReadModel,
        TimeProvider? timeProvider = null,
        IClientConnectionEpochStore? ownership = null) =>
        global::Akka.Actor.Props.Create(() => new PresenceActor(client, options, presenceReadModel, timeProvider: timeProvider, ownership: ownership));

    public static Props Props(ClientKey client, NetRatelAkkaOptions options, IActorRef presenceReadModel,
        IClientConnectionEpochStore ownership) => Props(client, options, presenceReadModel, timeProvider: null, ownership: ownership);

    private void HandleStartSession(StartGatewayPresenceSession message)
    {
        if (_ownership is not null) { HandleOwnedStart(message); return; }

        EnsureClient(message.Client);
        PrunePendingAdmissions();

        if (message.ProvisionalAdmission)
        {
            if (message.AdmissionExpiresAtUtc is not { } deadline || deadline <= _timeProvider.GetUtcNow() ||
                deadline <= message.ReceivedAtUtc || deadline > message.ReceivedAtUtc.AddSeconds(_options.GatewayAdmissionTimeoutSeconds))
            {
                ReplyStart(message, 0, PresenceMessageDisposition.AdmissionExpired);
                return;
            }
            if (_cancelledAdmissions.ContainsKey(message.ConnectionId))
            {
                ReplyStart(message, 0, PresenceMessageDisposition.AdmissionCancelled);
                return;
            }
            if (_admissionCancellationBarrierUntilUtc.HasValue || _cancelledAdmissions.Count >= MaximumCancelledAdmissions)
            {
                ReplyStart(message, 0, PresenceMessageDisposition.AdmissionCapacityExceeded);
                return;
            }
        }

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

        if (message.ProvisionalAdmission)
        {
            if (_pendingAdmissions.TryGetValue(message.ConnectionId, out var existing))
            {
                ReplyStart(message, existing.ConnectionEpoch, PresenceMessageDisposition.Duplicate);
                return;
            }
            if (_pendingAdmissions.Count >= MaximumPendingAdmissions)
            {
                ReplyStart(message, 0, PresenceMessageDisposition.AdmissionCapacityExceeded);
                return;
            }
            _lastIssuedEpoch = checked(_lastIssuedEpoch + 1);
            _pendingAdmissions.Add(message.ConnectionId, new(message, _lastIssuedEpoch));
            ScheduleAdmissionPrune();
            ReplyStart(message, _lastIssuedEpoch, PresenceMessageDisposition.Accepted);
            return;
        }

        _lastIssuedEpoch = checked(_lastIssuedEpoch + 1);
        CommitSession(message, _lastIssuedEpoch);
        ReplyStart(message, _lastIssuedEpoch, PresenceMessageDisposition.Accepted);
    }

    private void ReplyStart(StartGatewayPresenceSession message, long epoch, PresenceMessageDisposition disposition) =>
        Sender.Tell(new GatewayPresenceSessionStarted(_client, message.ConnectionId, epoch, disposition, message.ReceivedAtUtc));

    private void CommitSession(StartGatewayPresenceSession message, long connectionEpoch)
    {
        _activeEpoch = connectionEpoch;
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
        ScheduleExpiry(connectionEpoch, message.ConnectionId);
        ScheduleAuthenticationExpiry(connectionEpoch, message.ConnectionId);
        PublishTransition(message.ReceivedAtUtc, "connected");
        PublishReadModelSnapshot();

        _log.Info(
            "Gateway presence connected. authority={0}, client={1}, epoch={2}, connectionId={3}",
            "akka",
            _client,
            connectionEpoch,
            message.ConnectionId);
    }

    private void HandleHeartbeat(RecordGatewayHeartbeat message)
    {
        if (_ownership is not null) { HandleOwnedHeartbeat(message); return; }

        EnsureClient(message.Client);
        PrunePendingAdmissions();
        if (_pendingAdmissions.TryGetValue(message.ConnectionId, out var pending) &&
            pending.ConnectionEpoch == message.ConnectionEpoch)
        {
            if (pending.Message.AdmissionExpiresAtUtc <= message.ReceivedAtUtc ||
                pending.Message.AuthenticationExpiresAtUtc <= message.ReceivedAtUtc)
            {
                _pendingAdmissions.Remove(message.ConnectionId);
                ScheduleAdmissionPrune();
                Sender.Tell(CreateResult(PresenceMessageDisposition.AdmissionExpired));
                return;
            }
            if (message.Sequence == 0)
            {
                Sender.Tell(CreateResult(PresenceMessageDisposition.StaleSequence));
                return;
            }
            if (message.RenewedAuthenticationExpiresAtUtc is { } renewedPendingExpiry &&
                (pending.Message.AuthenticationExpiresAtUtc is not { } pendingAuthentication ||
                 renewedPendingExpiry <= pendingAuthentication || renewedPendingExpiry <= _timeProvider.GetUtcNow() ||
                 renewedPendingExpiry <= message.ReceivedAtUtc))
            {
                Sender.Tell(CreateResult(PresenceMessageDisposition.InvalidAuthenticationRenewal));
                return;
            }
            if (_activeEpoch is { } currentEpoch && pending.ConnectionEpoch <= currentEpoch)
            {
                _pendingAdmissions.Remove(message.ConnectionId);
                RememberCancelledAdmission(message.ConnectionId, pending.Message.AdmissionExpiresAtUtc!.Value);
                Sender.Tell(CreateResult(PresenceMessageDisposition.StaleConnectionEpoch));
                return;
            }
            _pendingAdmissions.Remove(message.ConnectionId);
            CommitSession(pending.Message, pending.ConnectionEpoch);
            ScheduleAdmissionPrune();
        }
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
        if (_ownership is not null) { HandleOwnedEnd(message); return; }

        EnsureClient(message.Client);
        if (message.CancelPendingAdmission)
        {
            PrunePendingAdmissions();
            _pendingAdmissions.TryGetValue(message.ConnectionId, out var pending);
            if (pending is not null && message.ConnectionEpoch != 0 && pending.ConnectionEpoch != message.ConnectionEpoch)
            {
                Sender.Tell(CreateResult(PresenceMessageDisposition.StaleConnectionEpoch));
                return;
            }
            _pendingAdmissions.Remove(message.ConnectionId);
            var retention = _timeProvider.GetUtcNow().AddSeconds(_options.GatewayAdmissionTimeoutSeconds);
            if (pending?.Message.AdmissionExpiresAtUtc is { } deadline && deadline > retention) retention = deadline;
            RememberCancelledAdmission(message.ConnectionId, retention);
            if (_activeConnectionId != message.ConnectionId)
            {
                Sender.Tell(CreateResult(PresenceMessageDisposition.Accepted));
                return;
            }
            if (message.ConnectionEpoch == 0) message = message with { ConnectionEpoch = _activeEpoch!.Value };
        }
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
        if (_ownership is not null) { HandleOwnedPresenceDeadline(message); return; }

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

    private void RememberCancelledAdmission(Guid connectionId, DateTimeOffset untilUtc)
    {
        if (_cancelledAdmissions.ContainsKey(connectionId))
            _cancelledAdmissions[connectionId] = untilUtc;
        else if (_cancelledAdmissions.Count < MaximumCancelledAdmissions)
            _cancelledAdmissions.Add(connectionId, untilUtc);
        else if (_admissionCancellationBarrierUntilUtc is not { } barrier || untilUtc > barrier)
            _admissionCancellationBarrierUntilUtc = untilUtc;
        ScheduleAdmissionPrune();
    }

    private void PrunePendingAdmissions()
    {
        if (_ownership is not null) { PruneOwnedAdmissions(); return; }

        var now = _timeProvider.GetUtcNow();
        foreach (var pending in _pendingAdmissions.Values.Where(candidate =>
                     candidate.Message.AdmissionExpiresAtUtc <= now || candidate.Message.AuthenticationExpiresAtUtc <= now).ToArray())
            _pendingAdmissions.Remove(pending.Message.ConnectionId);
        foreach (var cancelled in _cancelledAdmissions.Where(entry => entry.Value <= now).ToArray())
            _cancelledAdmissions.Remove(cancelled.Key);
        if (_admissionCancellationBarrierUntilUtc <= now) _admissionCancellationBarrierUntilUtc = null;
        ScheduleAdmissionPrune();
    }

    private void ScheduleAdmissionPrune()
    {
        if (_ownership is not null) { ScheduleOwnedAdmissionPrune(); return; }

        Timers.Cancel(AdmissionPruneTimerKey);
        var deadlines = _pendingAdmissions.Values.Select(candidate =>
            candidate.Message.AuthenticationExpiresAtUtc is { } authentication && authentication < candidate.Message.AdmissionExpiresAtUtc
                ? authentication : candidate.Message.AdmissionExpiresAtUtc!.Value)
            .Concat(_cancelledAdmissions.Values);
        if (_admissionCancellationBarrierUntilUtc is { } barrier) deadlines = deadlines.Append(barrier);
        var nearest = deadlines.Select(deadline => (DateTimeOffset?)deadline).Min();
        if (nearest is { } deadline)
            Timers.StartSingleTimer(AdmissionPruneTimerKey, new PruneAdmissions(),
                deadline > _timeProvider.GetUtcNow() ? deadline - _timeProvider.GetUtcNow() : TimeSpan.FromMilliseconds(1));
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
        if (_ownership is not null) { HandleOwnedAuthenticationDeadline(message); return; }

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
            IsAuthoritative: _ownership is null,
            LatencyMilliseconds: _latencyMilliseconds,
            LatencyMeasuredAtUtc: _latencyMeasuredAtUtc,
            LatencyExpiresAtUtc: _latencyMeasuredAtUtc + _options.HeartbeatTimeout,
            AuthenticationExpiresAtUtc: _authenticationExpiresAtUtc,
            OwnershipRevision: _ownership is null ? null : _lastAppliedOwnerRevision);

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
                IsAuthoritative: _ownership is null));
        }
    }

    private void EnsureClient(ClientKey client)
    {
        if (client != _client)
        {
            throw new InvalidOperationException($"Presence message for {client} reached actor for {_client}.");
        }
    }

    private sealed record PendingAdmission(StartGatewayPresenceSession Message, long ConnectionEpoch);
    private sealed record PruneAdmissions;
    internal sealed record PresenceDeadlineElapsed(long ConnectionEpoch, Guid ConnectionId);
    internal sealed record AuthenticationDeadlineElapsed(long ConnectionEpoch, Guid ConnectionId, DateTimeOffset ExpiresAtUtc);
}

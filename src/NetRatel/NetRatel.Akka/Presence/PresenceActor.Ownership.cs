using Akka.Actor;
using Akka.Pattern;
using NetRatel.Akka.Observability;
using NetRatel.Application.Presence;

namespace NetRatel.Akka.Presence;

// Work-only candidate. No compiler or runtime receipt. The SQL store and real
// model must be integrated against refreshed #148 before these files are used.
public sealed partial class PresenceActor
{
    private readonly IClientConnectionEpochStore? _ownership;
    private readonly Guid _incarnation = Guid.NewGuid();
    private readonly Dictionary<Guid, OwnedAdmission> _ownedAdmissions = [];
    private readonly Dictionary<Guid, OwnedOperation> _ownedOperations = [];
    private long _generation;
    private long _ownerGeneration;
    private long _lastAppliedOwnerRevision;
    private long _lastObservedOwnerRevision;
    private bool _ownedLocalClosing;
    private Guid? _ownerHeartbeatOperation;
    private DateTimeOffset? _presenceExpiresAtUtc;
    private StartGatewayPresenceSession? _ownedActiveStart;

    private enum OwnedPhase { Reserving, Reserved, Committing, Cancelled }
    private enum OwnedKind { Reserve, Commit, Heartbeat, Cancel, Retire, StartRetry }
    private sealed record OwnedAdmission(long Generation, StartGatewayPresenceSession Start,
        IActorRef Reply, OwnedPhase Phase, Reservation? Reservation = null);
    private sealed record OwnedOperation(Guid Id, Guid Incarnation, long Generation, OwnedKind Kind,
        Guid ConnectionId, OwnerKey? Owner, IActorRef Reply, CancellationTokenSource Cancellation, DateTimeOffset DeadlineUtc,
        StartGatewayPresenceSession? Start = null, RecordGatewayHeartbeat? Heartbeat = null,
        EndGatewayPresenceSession? End = null, Reservation? Reservation = null,
        RetirementReason? Reason = null, DateTimeOffset? ExpectedDeadline = null,
        bool ReplySent = false, bool Invalidated = false);
    private sealed record OwnedCompleted(Guid Incarnation, Guid OperationId, object? Result, Exception? Error);
    private sealed record OwnedTimedOut(Guid Incarnation, Guid OperationId);
    private static string OwnedTimerKey(Guid operation) => "gateway-owner-operation/" + operation.ToString("N");

    private void RegisterOwnershipHandlers()
    {
        Receive<OwnedCompleted>(message =>
        {
            if (Sender.Equals(Self) && message.Incarnation == _incarnation) HandleOwnedCompleted(message);
        });
        Receive<OwnedTimedOut>(message =>
        {
            if (message.Incarnation == _incarnation) HandleOwnedTimeout(message.OperationId);
        });
    }

    private void HandleOwnedStart(StartGatewayPresenceSession input)
    {
        EnsureClient(input.Client);
        PruneOwnedAdmissions();
        var now = _timeProvider.GetUtcNow();
        // Production may not fall back to main's actor-only, nonprovisional
        // compatibility path or create machine authority without signed expiry.
        if (!input.ProvisionalAdmission || input.AdmissionExpiresAtUtc is not { } deadline ||
            deadline <= now || deadline <= input.ReceivedAtUtc ||
            deadline > input.ReceivedAtUtc.AddSeconds(_options.GatewayAdmissionTimeoutSeconds))
        { ReplyStart(input, 0, PresenceMessageDisposition.AdmissionExpired); return; }
        if (input.AuthenticationExpiresAtUtc is not { } authentication || authentication <= now || authentication <= input.ReceivedAtUtc)
        { ReplyStart(input, 0, PresenceMessageDisposition.AuthenticationExpired); return; }
        if (_cancelledAdmissions.ContainsKey(input.ConnectionId))
        { ReplyStart(input, 0, PresenceMessageDisposition.AdmissionCancelled); return; }
        if (_admissionCancellationBarrierUntilUtc.HasValue || _cancelledAdmissions.Count >= MaximumCancelledAdmissions)
        { ReplyStart(input, 0, PresenceMessageDisposition.AdmissionCapacityExceeded); return; }
        if (input.ConnectionId == Guid.Empty || input.OperationId == Guid.Empty)
        { ReplyStart(input, 0, PresenceMessageDisposition.ConnectionMismatch); return; }

        // Freeze mutable list input before any asynchronous call.
        var start = input with { Capabilities = input.Capabilities.ToArray() };
        if (_ownedAdmissions.TryGetValue(start.ConnectionId, out var existing))
        {
            if (!SameStart(existing.Start, start))
            { ReplyStart(start, 0, PresenceMessageDisposition.ConnectionMismatch); return; }
            if (existing.Phase == OwnedPhase.Reserved && existing.Reservation is { } reservation)
                ReplyStart(start, reservation.Owner.Epoch, PresenceMessageDisposition.Duplicate);
            else ReplyUnavailable(Sender);
            return;
        }
        if (_activeConnectionId == start.ConnectionId && _activeEpoch is { } activeEpoch)
        {
            if (_ownedActiveStart is null || !SameStart(_ownedActiveStart, start))
            { ReplyStart(start, 0, PresenceMessageDisposition.ConnectionMismatch); return; }
            if (_ownedOperations.Values.Count(x => x.Kind == OwnedKind.StartRetry) >= 2)
            { ReplyUnavailable(Sender); return; }
            var retry = NewOperation(OwnedKind.StartRetry, start.ConnectionId, _ownerGeneration,
                new(_client, start.ConnectionId, activeEpoch), Sender, start: start);
            Dispatch(retry, token => _ownership!.GetCurrentAsync(_client, token));
            return;
        }
        // Entries remain bounded even if a cancelled store call has not yet
        // completed. An unrelated reserve never uses the active heartbeat lane.
        if (_ownedAdmissions.Count >= MaximumPendingAdmissions)
        { ReplyStart(start, 0, PresenceMessageDisposition.AdmissionCapacityExceeded); return; }
        var generation = checked(++_generation);
        _ownedAdmissions.Add(start.ConnectionId, new(generation, start, Sender, OwnedPhase.Reserving));
        var request = new AdmissionRequest(_client, start.ConnectionId, start.OperationId, _lastIssuedEpoch,
            start.ReceivedAtUtc, deadline, authentication,
            new(start.AgentVersion, start.Capabilities, start.LegacySpacetimeIdentity));
        var operation = NewOperation(OwnedKind.Reserve, start.ConnectionId, generation, null, Sender,
            absoluteDeadline: Minimum(deadline, authentication), start: start);
        Dispatch(operation, token => _ownership!.ReserveAsync(request, token));
        ScheduleOwnedAdmissionPrune();
    }

    private void HandleOwnedHeartbeat(RecordGatewayHeartbeat message)
    {
        EnsureClient(message.Client);
        PruneOwnedAdmissions();
        var now = _timeProvider.GetUtcNow();
        if (_ownedAdmissions.TryGetValue(message.ConnectionId, out var admission))
        {
            if (admission.Reservation is not { } reservation || admission.Phase != OwnedPhase.Reserved)
            { ReplyUnavailable(Sender); return; }
            if (reservation.Owner.Epoch != message.ConnectionEpoch)
            { Sender.Tell(CreateResult(PresenceMessageDisposition.StaleConnectionEpoch)); return; }
            if (message.Sequence == 0)
            { Sender.Tell(CreateResult(PresenceMessageDisposition.StaleSequence)); return; }
            var start = admission.Start;
            if (start.AdmissionExpiresAtUtc <= now || start.AdmissionExpiresAtUtc <= message.ReceivedAtUtc ||
                start.AuthenticationExpiresAtUtc <= now || start.AuthenticationExpiresAtUtc <= message.ReceivedAtUtc)
            {
                InvalidateAdmission(message.ConnectionId);
                Sender.Tell(CreateResult(PresenceMessageDisposition.AdmissionExpired));
                return;
            }
            if (!ValidRenewal(message, start.AuthenticationExpiresAtUtc, now))
            { Sender.Tell(CreateResult(PresenceMessageDisposition.InvalidAuthenticationRenewal)); return; }
            if (_activeEpoch is { } current && reservation.Owner.Epoch <= current)
            {
                InvalidateAdmission(message.ConnectionId);
                Sender.Tell(CreateResult(PresenceMessageDisposition.StaleConnectionEpoch));
                return;
            }
            _ownedAdmissions[message.ConnectionId] = admission with { Phase = OwnedPhase.Committing };
            var operation = NewOperation(OwnedKind.Commit, message.ConnectionId, admission.Generation,
                reservation.Owner, Sender, absoluteDeadline: Minimum(start.AdmissionExpiresAtUtc!.Value,
                    start.AuthenticationExpiresAtUtc!.Value, message.ReceivedAtUtc + _options.HeartbeatTimeout),
                heartbeat: message, reservation: reservation, start: start);
            var request = Heartbeat(message, reservation.Owner);
            Dispatch(operation, token => ConfirmWriteAsync(_ownership!, reservation.Owner,
                ct => _ownership!.CommitAsync(reservation, request, ct), token));
            return;
        }
        var disposition = ValidateActiveSession(message.ConnectionId, message.ConnectionEpoch);
        if (disposition != PresenceMessageDisposition.Accepted || _status != ClientPresenceStatus.Online || _ownedLocalClosing)
        { Sender.Tell(CreateResult(disposition == PresenceMessageDisposition.Accepted ? PresenceMessageDisposition.NoActiveSession : disposition)); return; }
        if (_ownerHeartbeatOperation.HasValue || _ownedOperations.Values.Count(x => x.Kind == OwnedKind.Heartbeat) >= MaximumPendingAdmissions)
        { ReplyUnavailable(Sender); return; }
        if (message.Sequence == 0)
        { Sender.Tell(CreateResult(PresenceMessageDisposition.StaleSequence)); return; }
        if (!ValidRenewal(message, _authenticationExpiresAtUtc, now))
        { Sender.Tell(CreateResult(PresenceMessageDisposition.InvalidAuthenticationRenewal)); return; }
        var owner = new OwnerKey(_client, message.ConnectionId, message.ConnectionEpoch);
        if (_authenticationExpiresAtUtc <= now || _authenticationExpiresAtUtc <= message.ReceivedAtUtc)
        {
            StartOwnedRetirement(owner, RetirementReason.AuthenticationExpiry, _authenticationExpiresAtUtc, ActorRefs.Nobody);
            Sender.Tell(CreateResult(PresenceMessageDisposition.AuthenticationExpired));
            return;
        }
        if (_presenceExpiresAtUtc <= now)
        {
            StartOwnedRetirement(owner, RetirementReason.HeartbeatExpiry, _presenceExpiresAtUtc, ActorRefs.Nobody);
            Sender.Tell(CreateResult(PresenceMessageDisposition.NoActiveSession));
            return;
        }
        var activeOperation = NewOperation(OwnedKind.Heartbeat, message.ConnectionId, _ownerGeneration, owner, Sender,
            absoluteDeadline: Minimum(_authenticationExpiresAtUtc!.Value, _presenceExpiresAtUtc!.Value), heartbeat: message);
        _ownerHeartbeatOperation = activeOperation.Id;
        var heartbeat = Heartbeat(message, owner);
        Dispatch(activeOperation, token => ConfirmWriteAsync(_ownership!, owner,
            ct => message.RenewedAuthenticationExpiresAtUtc.HasValue ? _ownership!.RenewAsync(heartbeat, ct) :
                _ownership!.RecordHeartbeatAsync(heartbeat, ct), token));
    }

    private void HandleOwnedEnd(EndGatewayPresenceSession message)
    {
        EnsureClient(message.Client);
        PruneOwnedAdmissions();
        if (_ownedAdmissions.TryGetValue(message.ConnectionId, out var admission) &&
            message.ConnectionEpoch != 0 && admission.Reservation is { } reservation && reservation.Owner.Epoch != message.ConnectionEpoch)
        { Sender.Tell(CreateResult(PresenceMessageDisposition.StaleConnectionEpoch)); return; }
        var localOwner = _activeConnectionId == message.ConnectionId && _activeEpoch is { } epoch ?
            new OwnerKey(_client, message.ConnectionId, epoch) : (OwnerKey?)null;
        if (!message.CancelPendingAdmission && localOwner is null)
        { Sender.Tell(CreateResult(PresenceMessageDisposition.NoActiveSession)); return; }
        if (localOwner is { } exact && message.ConnectionEpoch != exact.Epoch && !(message.CancelPendingAdmission && message.ConnectionEpoch == 0))
        { Sender.Tell(CreateResult(PresenceMessageDisposition.StaleConnectionEpoch)); return; }
        InvalidateAdmission(message.ConnectionId, issueCleanup: false);
        RememberOwnedCancellation(message.ConnectionId, message.ReceivedAtUtc.AddSeconds(_options.GatewayAdmissionTimeoutSeconds));
        foreach (var operation in _ownedOperations.Values.Where(x => x.ConnectionId == message.ConnectionId &&
                     x.Kind is OwnedKind.Heartbeat or OwnedKind.Commit or OwnedKind.Reserve).ToArray())
            InvalidateOperation(operation, notify: true, issueCleanup: false);
        if (localOwner.HasValue) _ownedLocalClosing = true;
        StartOwnedCancel(message.ConnectionId, message.ConnectionEpoch > 0 ? message.ConnectionEpoch : null,
            message.ReceivedAtUtc, Sender, message, localOwner);
    }

    private void HandleOwnedPresenceDeadline(PresenceDeadlineElapsed message)
    {
        if (_activeEpoch == message.ConnectionEpoch && _activeConnectionId == message.ConnectionId && _presenceExpiresAtUtc is { } deadline)
            StartOwnedRetirement(new(_client, message.ConnectionId, message.ConnectionEpoch), RetirementReason.HeartbeatExpiry, deadline, ActorRefs.Nobody);
    }

    private void HandleOwnedAuthenticationDeadline(AuthenticationDeadlineElapsed message)
    {
        if (_activeEpoch == message.ConnectionEpoch && _activeConnectionId == message.ConnectionId && _authenticationExpiresAtUtc == message.ExpiresAtUtc)
            StartOwnedRetirement(new(_client, message.ConnectionId, message.ConnectionEpoch), RetirementReason.AuthenticationExpiry, message.ExpiresAtUtc, ActorRefs.Nobody);
    }

    private void StartOwnedRetirement(OwnerKey owner, RetirementReason reason, DateTimeOffset? deadline, IActorRef reply)
    {
        if (reason != RetirementReason.ExplicitClose && deadline is null) return;
        if (_ownedOperations.Values.Any(x => x.Kind == OwnedKind.Retire && x.Owner == owner && x.Reason == reason && x.ExpectedDeadline == deadline)) return;
        if (_ownedOperations.Values.Count(x => x.Kind == OwnedKind.Retire) >= 2) return;
        var operation = NewOperation(OwnedKind.Retire, owner.ConnectionId, _ownerGeneration, owner, reply,
            reason: reason, expectedDeadline: deadline);
        Dispatch(operation, token => _ownership!.RetireAsync(owner, reason, deadline, token));
    }

    private void StartOwnedCancel(Guid connectionId, long? epoch, DateTimeOffset receivedAt, IActorRef reply,
        EndGatewayPresenceSession? end = null, OwnerKey? localOwner = null)
    {
        localOwner ??= epoch is > 0 ? new OwnerKey(_client, connectionId, epoch.Value) : null;
        if (_ownedOperations.Values.Any(x => x.Kind == OwnedKind.Cancel && x.ConnectionId == connectionId))
        { if (!reply.IsNobody()) ReplyUnavailable(reply); return; }
        if (_ownedOperations.Values.Count(x => x.Kind == OwnedKind.Cancel) >= MaximumPendingAdmissions)
        {
            RememberOwnedCancellation(connectionId, receivedAt.AddSeconds(_options.GatewayAdmissionTimeoutSeconds));
            if (localOwner is { } exact) StartOwnedRetirement(exact, RetirementReason.ExplicitClose, null, ActorRefs.Nobody);
            if (!reply.IsNobody()) ReplyUnavailable(reply);
            return;
        }
        var operation = NewOperation(OwnedKind.Cancel, connectionId, _ownerGeneration, localOwner, reply, end: end);
        Dispatch(operation, async token =>
        {
            // Exact current-owner retirement uses the owner row independently
            // while cancellation journal work may wait behind a reserve lock.
            var cancellation = _ownership!.CancelAdmissionAsync(_client, connectionId, epoch, receivedAt, token);
            var retirement = localOwner is { } exact ? _ownership!.RetireAsync(exact, RetirementReason.ExplicitClose, null, token) :
                Task.FromResult(new WriteResult(OwnershipDisposition.Accepted));
            await Task.WhenAll(cancellation, retirement).ConfigureAwait(false);
            var result = await cancellation.ConfigureAwait(false);
            return result with { Current = await _ownership!.GetCurrentAsync(_client, token).ConfigureAwait(false) };
        });
    }

    private OwnedOperation NewOperation(OwnedKind kind, Guid connectionId, long generation, OwnerKey? owner, IActorRef reply,
        DateTimeOffset? absoluteDeadline = null, StartGatewayPresenceSession? start = null, RecordGatewayHeartbeat? heartbeat = null,
        EndGatewayPresenceSession? end = null, Reservation? reservation = null, RetirementReason? reason = null, DateTimeOffset? expectedDeadline = null)
    {
        var now = _timeProvider.GetUtcNow();
        var budget = absoluteDeadline is { } deadline ? deadline - now : _options.AskTimeout;
        if (budget > _options.AskTimeout) budget = _options.AskTimeout;
        if (budget <= TimeSpan.Zero) budget = TimeSpan.FromTicks(1);
        var id = Guid.NewGuid();
        var operation = new OwnedOperation(id, _incarnation, generation, kind, connectionId, owner, reply,
            new CancellationTokenSource(budget, _timeProvider), now + budget, start, heartbeat, end, reservation, reason, expectedDeadline);
        _ownedOperations.Add(id, operation);
        Timers.StartSingleTimer(OwnedTimerKey(id), new OwnedTimedOut(_incarnation, id), budget);
        return operation;
    }

    private void Dispatch<T>(OwnedOperation operation, Func<CancellationToken, Task<T>> action)
    {
        Task<T> task;
        try { task = action(operation.Cancellation.Token); }
        catch (Exception error) { task = Task.FromException<T>(error); }
        // Continuations capture immutable tags/reply metadata only. They never
        // access Sender, actor state, Context or an actor-owned DbContext.
        task.PipeTo(Self, Self,
            success: result => new OwnedCompleted(operation.Incarnation, operation.Id, result, null),
            failure: error => new OwnedCompleted(operation.Incarnation, operation.Id, null, error));
    }

    private static async Task<WriteResult> ConfirmWriteAsync(IClientConnectionEpochStore store, OwnerKey owner,
        Func<CancellationToken, Task<WriteResult>> write, CancellationToken token)
    {
        var result = await write(token).ConfigureAwait(false);
        if (result.Disposition is not (OwnershipDisposition.Accepted or OwnershipDisposition.Duplicate)) return result;
        token.ThrowIfCancellationRequested();
        var current = await store.GetCurrentAsync(owner.Client, token).ConfigureAwait(false);
        return current is null || current.Owner != owner ? new(OwnershipDisposition.StaleEpoch, current) :
            !current.Active ? new(OwnershipDisposition.NoActiveSession, current) : result with { Current = current };
    }

    private void HandleOwnedCompleted(OwnedCompleted message)
    {
        if (!_ownedOperations.Remove(message.OperationId, out var operation) || operation.Incarnation != _incarnation) return;
        Timers.Cancel(OwnedTimerKey(operation.Id));
        // Cancellation callbacks and actor timers have independent delivery.
        // A queued successful DB result cannot acknowledge after this owned
        // operation's original budget just because its timer is behind it.
        var deadlineElapsed = operation.Cancellation.IsCancellationRequested ||
            operation.DeadlineUtc <= _timeProvider.GetUtcNow();
        operation.Cancellation.Dispose();
        if (_ownerHeartbeatOperation == operation.Id) _ownerHeartbeatOperation = null;
        if (operation.Invalidated || deadlineElapsed || message.Error is not null)
        {
            if (!operation.ReplySent && !operation.Reply.IsNobody()) ReplyUnavailable(operation.Reply);
            if (operation.Kind is OwnedKind.Reserve or OwnedKind.Commit)
            {
                RemoveAdmissionIfGeneration(operation.ConnectionId, operation.Generation);
                StartOwnedCancel(operation.ConnectionId, operation.Owner?.Epoch,
                    _timeProvider.GetUtcNow(), ActorRefs.Nobody);
            }
            ScheduleOwnedAdmissionPrune();
            return;
        }
        if (message.Result is WriteResult { Current: { } observed })
            _lastObservedOwnerRevision = Math.Max(_lastObservedOwnerRevision, observed.Revision);
        if (message.Result is OwnerSnapshot readOwner)
            _lastObservedOwnerRevision = Math.Max(_lastObservedOwnerRevision, readOwner.Revision);
        switch (operation.Kind)
        {
            case OwnedKind.Reserve: CompleteOwnedReserve(operation, (ReserveResult)message.Result!); break;
            case OwnedKind.Commit: CompleteOwnedCommit(operation, (WriteResult)message.Result!); break;
            case OwnedKind.Heartbeat: CompleteOwnedHeartbeat(operation, (WriteResult)message.Result!); break;
            case OwnedKind.Cancel: CompleteOwnedCancel(operation, (WriteResult)message.Result!); break;
            case OwnedKind.Retire: CompleteOwnedRetire(operation, (WriteResult)message.Result!); break;
            case OwnedKind.StartRetry: CompleteOwnedStartRetry(operation, (OwnerSnapshot?)message.Result); break;
        }
        ScheduleOwnedAdmissionPrune();
    }

    private void CompleteOwnedReserve(OwnedOperation operation, ReserveResult result)
    {
        if (!_ownedAdmissions.TryGetValue(operation.ConnectionId, out var admission) || admission.Generation != operation.Generation ||
            admission.Phase != OwnedPhase.Reserving || !AdmissionStillValid(admission))
        {
            RemoveAdmissionIfGeneration(operation.ConnectionId, operation.Generation);
            ReplyUnavailable(operation.Reply);
            StartOwnedCancel(operation.ConnectionId, result.Reservation?.Owner.Epoch, _timeProvider.GetUtcNow(), ActorRefs.Nobody);
            return;
        }
        if (result.Reservation is not { } reserved || result.Disposition is not (OwnershipDisposition.Accepted or OwnershipDisposition.Duplicate))
        {
            _ownedAdmissions.Remove(operation.ConnectionId);
            operation.Reply.Tell(new GatewayPresenceSessionStarted(_client, operation.ConnectionId, 0,
                MapOwnership(result.Disposition), admission.Start.ReceivedAtUtc));
            return;
        }
        if (reserved.Owner.Client != _client || reserved.Owner.ConnectionId != operation.ConnectionId || reserved.OperationId != admission.Start.OperationId ||
            reserved.AdmissionExpiresAtUtc > admission.Start.AdmissionExpiresAtUtc || reserved.AuthenticationExpiresAtUtc > admission.Start.AuthenticationExpiresAtUtc)
        {
            InvalidateAdmission(operation.ConnectionId);
            ReplyUnavailable(operation.Reply);
            return;
        }
        _lastIssuedEpoch = Math.Max(_lastIssuedEpoch, reserved.Owner.Epoch); // allocator hint, never authority
        _ownedAdmissions[operation.ConnectionId] = admission with { Phase = OwnedPhase.Reserved, Reservation = reserved };
        operation.Reply.Tell(new GatewayPresenceSessionStarted(_client, operation.ConnectionId, reserved.Owner.Epoch,
            MapOwnership(result.Disposition), admission.Start.ReceivedAtUtc));
    }

    private void CompleteOwnedCommit(OwnedOperation operation, WriteResult result)
    {
        if (!_ownedAdmissions.TryGetValue(operation.ConnectionId, out var admission) || admission.Generation != operation.Generation ||
            admission.Phase != OwnedPhase.Committing || !AdmissionStillValid(admission))
        {
            RemoveAdmissionIfGeneration(operation.ConnectionId, operation.Generation);
            ReplyUnavailable(operation.Reply);
            StartOwnedCancel(operation.ConnectionId, operation.Owner?.Epoch, _timeProvider.GetUtcNow(), ActorRefs.Nobody);
            return; // ambiguous committed result: no stale Online or heartbeat ACK
        }
        _ownedAdmissions.Remove(operation.ConnectionId);
        if (result.Disposition is OwnershipDisposition.Accepted or OwnershipDisposition.Duplicate && result.Current is { } current &&
            current.Owner == operation.Owner && operation.Reservation is { } exactReservation &&
            current.StartOperationId == exactReservation.OperationId &&
            current.AdmissionPayloadHash == exactReservation.PayloadHash &&
            current.IsEffective(_timeProvider.GetUtcNow()) && current.Revision >= _lastObservedOwnerRevision && current.Revision > _lastAppliedOwnerRevision)
        {
            _ownerGeneration = checked(++_generation);
            _ownedActiveStart = admission.Start;
            _ownedLocalClosing = false;
            foreach (var oldHeartbeat in _ownedOperations.Values.Where(x => x.Kind == OwnedKind.Heartbeat && x.Owner != current.Owner).ToArray())
                InvalidateOperation(oldHeartbeat, notify: true, issueCleanup: false);
            _ownerHeartbeatOperation = null;
            ApplyOwnedSnapshot(current, "connected", operation.Heartbeat);
            operation.Reply.Tell(new PresenceMessageResult(_client, current.Owner.Epoch, MapOwnership(result.Disposition), current.Sequence));
        }
        else
        {
            operation.Reply.Tell(new PresenceMessageResult(_client, operation.Owner?.Epoch,
                result.Disposition is OwnershipDisposition.Accepted or OwnershipDisposition.Duplicate ? PresenceMessageDisposition.StaleConnectionEpoch :
                    MapOwnership(result.Disposition), result.Current?.Sequence ?? 0));
            StartOwnedCancel(operation.ConnectionId, operation.Owner?.Epoch, _timeProvider.GetUtcNow(), ActorRefs.Nobody);
        }
    }

    private void CompleteOwnedHeartbeat(OwnedOperation operation, WriteResult result)
    {
        if (!MatchesLocalOwner(operation) || _ownedLocalClosing)
        { operation.Reply.Tell(CreateResult(PresenceMessageDisposition.StaleConnectionEpoch)); return; }
        if (result.Current is { } current && current.Owner == operation.Owner &&
            current.Revision >= _lastObservedOwnerRevision && current.IsEffective(_timeProvider.GetUtcNow()))
        {
            // A rejected sequence or renewal never retires a valid owner.
            // Duplicate SQL results never extend either durable deadline.
            ApplyOwnedSnapshot(current, "heartbeat", operation.Heartbeat);
            operation.Reply.Tell(CreateResult(MapOwnership(result.Disposition)));
        }
        else
        {
            InvalidateLocalOwner(operation.Owner!.Value, operation.Generation, "ownership-unavailable");
            operation.Reply.Tell(CreateResult(result.Disposition is OwnershipDisposition.Accepted or OwnershipDisposition.Duplicate ?
                PresenceMessageDisposition.StaleConnectionEpoch : MapOwnership(result.Disposition)));
        }
    }

    private void CompleteOwnedCancel(OwnedOperation operation, WriteResult result)
    {
        RemoveCancelledAdmissionIfIdle(operation.ConnectionId);
        if (operation.Owner is { } owner && MatchesLocalOwner(operation))
        {
            if (result.Current is { } current && current.Owner == owner) ApplyOwnedSnapshot(current, "disconnected");
            else InvalidateLocalOwner(owner, operation.Generation, "ownership-replaced");
        }
        if (!operation.Reply.IsNobody()) operation.Reply.Tell(CreateResult(MapOwnership(result.Disposition)));
    }

    private void CompleteOwnedRetire(OwnedOperation operation, WriteResult result)
    {
        if (!MatchesLocalOwner(operation)) return;
        if (result.Current is { } stale && stale.Revision < _lastObservedOwnerRevision) return;
        if (result.Current is { } current && current.Owner == operation.Owner)
            ApplyOwnedSnapshot(current, operation.Reason == RetirementReason.AuthenticationExpiry ? "authentication-expired" : "heartbeat-expired");
        else InvalidateLocalOwner(operation.Owner!.Value, operation.Generation, "ownership-replaced");
    }

    private void CompleteOwnedStartRetry(OwnedOperation operation, OwnerSnapshot? current)
    {
        var disposition = current is not null && current.Owner == operation.Owner && current.IsEffective(_timeProvider.GetUtcNow()) && MatchesLocalOwner(operation)
            ? PresenceMessageDisposition.Duplicate : PresenceMessageDisposition.NoActiveSession;
        if (disposition != PresenceMessageDisposition.Duplicate && operation.Owner is { } expected)
            InvalidateLocalOwner(expected, operation.Generation, "ownership-unavailable");
        operation.Reply.Tell(new GatewayPresenceSessionStarted(_client, operation.ConnectionId,
            disposition == PresenceMessageDisposition.Duplicate ? current!.Owner.Epoch : 0, disposition, operation.Start!.ReceivedAtUtc));
    }

    private void ApplyOwnedSnapshot(OwnerSnapshot snapshot, string reason, RecordGatewayHeartbeat? heartbeat = null)
    {
        if (snapshot.Revision <= _lastAppliedOwnerRevision || snapshot.Revision < _lastObservedOwnerRevision) return;
        var wasOnline = _status == ClientPresenceStatus.Online;
        var replaced = _activeConnectionId != snapshot.Owner.ConnectionId || _activeEpoch != snapshot.Owner.Epoch;
        _lastAppliedOwnerRevision = snapshot.Revision;
        _activeEpoch = snapshot.Owner.Epoch; _activeConnectionId = snapshot.Owner.ConnectionId;
        _lastAcceptedSequence = snapshot.Sequence; _lastReceivedAtUtc = snapshot.LastReceivedAtUtc;
        _presenceExpiresAtUtc = snapshot.PresenceExpiresAtUtc; _authenticationExpiresAtUtc = snapshot.AuthenticationExpiresAtUtc;
        _agentVersion = snapshot.Metadata.AgentVersion; _capabilities = snapshot.Metadata.Capabilities.ToArray();
        _legacySpacetimeIdentity = snapshot.Metadata.LegacySpacetimeIdentity;
        if (replaced) ClearLatency();
        if (heartbeat?.HeartbeatRoundTripMilliseconds is { } latency && double.IsFinite(latency) && latency >= 0 &&
            latency <= _options.HeartbeatTimeout.TotalMilliseconds && heartbeat.LatencyMeasuredAtUtc is { } measured &&
            measured <= heartbeat.ReceivedAtUtc && heartbeat.ReceivedAtUtc - measured <= _options.HeartbeatTimeout)
        { _latencyMilliseconds = latency; _latencyMeasuredAtUtc = measured; }
        _status = !_ownedLocalClosing && snapshot.IsEffective(_timeProvider.GetUtcNow()) ? ClientPresenceStatus.Online : ClientPresenceStatus.Offline;
        Timers.Cancel(ExpiryTimerKey); Timers.Cancel(AuthenticationExpiryTimerKey);
        if (_status == ClientPresenceStatus.Online)
        {
            var now = _timeProvider.GetUtcNow();
            Timers.StartSingleTimer(ExpiryTimerKey, new PresenceDeadlineElapsed(snapshot.Owner.Epoch, snapshot.Owner.ConnectionId), PositiveTimerDelay(snapshot.PresenceExpiresAtUtc - now));
            Timers.StartSingleTimer(AuthenticationExpiryTimerKey,
                new AuthenticationDeadlineElapsed(snapshot.Owner.Epoch, snapshot.Owner.ConnectionId, snapshot.AuthenticationExpiresAtUtc), PositiveTimerDelay(snapshot.AuthenticationExpiresAtUtc - now));
            if (!wasOnline) NetRatelAkkaTelemetry.PresenceClientConnected();
        }
        else
        {
            ClearLatency();
            if (wasOnline) NetRatelAkkaTelemetry.PresenceClientDisconnected();
            _ownerGeneration = checked(++_generation);
        }
        if (replaced || wasOnline != (_status == ClientPresenceStatus.Online)) PublishTransition(snapshot.LastReceivedAtUtc, reason);
        PublishReadModelSnapshot(); // physical projection only; shared read is separate
    }

    private void InvalidateLocalOwner(OwnerKey owner, long generation, string reason)
    {
        if (_ownerGeneration != generation || _activeConnectionId != owner.ConnectionId || _activeEpoch != owner.Epoch) return;
        var wasOnline = _status == ClientPresenceStatus.Online;
        _status = ClientPresenceStatus.Offline; _ownerGeneration = checked(++_generation);
        Timers.Cancel(ExpiryTimerKey); Timers.Cancel(AuthenticationExpiryTimerKey); ClearLatency();
        if (wasOnline) { NetRatelAkkaTelemetry.PresenceClientDisconnected(); PublishTransition(_timeProvider.GetUtcNow(), reason); }
        PublishReadModelSnapshot();
    }

    private void HandleOwnedTimeout(Guid operationId)
    {
        if (_ownedOperations.TryGetValue(operationId, out var operation)) InvalidateOperation(operation, notify: true);
    }

    private void InvalidateOperation(OwnedOperation operation, bool notify, bool issueCleanup = true)
    {
        if (operation.Invalidated) return;
        operation.Cancellation.Cancel();
        var sent = operation.ReplySent;
        if (notify && !sent && !operation.Reply.IsNobody()) { ReplyUnavailable(operation.Reply); sent = true; }
        _ownedOperations[operation.Id] = operation with { Invalidated = true, ReplySent = sent };
        if (operation.Kind is OwnedKind.Reserve or OwnedKind.Commit)
        {
            if (_ownedAdmissions.TryGetValue(operation.ConnectionId, out var admission) && admission.Generation == operation.Generation)
                _ownedAdmissions[operation.ConnectionId] = admission with { Phase = OwnedPhase.Cancelled };
            RememberOwnedCancellation(operation.ConnectionId, operation.Start!.AdmissionExpiresAtUtc!.Value);
            if (issueCleanup) StartOwnedCancel(operation.ConnectionId, operation.Owner?.Epoch, _timeProvider.GetUtcNow(), ActorRefs.Nobody);
        }
    }

    private void InvalidateAdmission(Guid connectionId, bool issueCleanup = true)
    {
        if (!_ownedAdmissions.TryGetValue(connectionId, out var admission)) return;
        _ownedAdmissions[connectionId] = admission with { Phase = OwnedPhase.Cancelled };
        RememberOwnedCancellation(connectionId, admission.Start.AdmissionExpiresAtUtc!.Value);
        foreach (var operation in _ownedOperations.Values.Where(x => x.ConnectionId == connectionId &&
                     x.Kind is OwnedKind.Reserve or OwnedKind.Commit).ToArray()) InvalidateOperation(operation, notify: true, issueCleanup: false);
        if (issueCleanup) StartOwnedCancel(connectionId, admission.Reservation?.Owner.Epoch, _timeProvider.GetUtcNow(), ActorRefs.Nobody);
    }

    private void PruneOwnedAdmissions()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var admission in _ownedAdmissions.Values.Where(x => x.Phase != OwnedPhase.Cancelled &&
                     (x.Start.AdmissionExpiresAtUtc <= now || x.Start.AuthenticationExpiresAtUtc <= now)).ToArray())
            InvalidateAdmission(admission.Start.ConnectionId);
        foreach (var cancelled in _cancelledAdmissions.Where(x => x.Value <= now).ToArray()) _cancelledAdmissions.Remove(cancelled.Key);
        if (_admissionCancellationBarrierUntilUtc <= now) _admissionCancellationBarrierUntilUtc = null;
        foreach (var admission in _ownedAdmissions.Values.Where(x => x.Phase == OwnedPhase.Cancelled).ToArray()) RemoveCancelledAdmissionIfIdle(admission.Start.ConnectionId);
        ScheduleOwnedAdmissionPrune();
    }

    private void RememberOwnedCancellation(Guid connectionId, DateTimeOffset deadline)
    {
        if (!_cancelledAdmissions.ContainsKey(connectionId))
        {
            if (_cancelledAdmissions.Count < MaximumCancelledAdmissions) _cancelledAdmissions.Add(connectionId, deadline);
            else if (_admissionCancellationBarrierUntilUtc is not { } barrier || deadline > barrier) _admissionCancellationBarrierUntilUtc = deadline;
        }
        ScheduleOwnedAdmissionPrune();
    }

    private void ScheduleOwnedAdmissionPrune()
    {
        Timers.Cancel(AdmissionPruneTimerKey);
        var deadlines = _ownedAdmissions.Values.Where(x => x.Phase != OwnedPhase.Cancelled)
            .Select(x => Minimum(x.Start.AdmissionExpiresAtUtc!.Value, x.Start.AuthenticationExpiresAtUtc!.Value))
            .Concat(_cancelledAdmissions.Values);
        if (_admissionCancellationBarrierUntilUtc is { } barrier) deadlines = deadlines.Append(barrier);
        var next = deadlines.Select(x => (DateTimeOffset?)x).Min();
        if (next is { } deadline) Timers.StartSingleTimer(AdmissionPruneTimerKey, new PruneAdmissions(),
            deadline > _timeProvider.GetUtcNow() ? deadline - _timeProvider.GetUtcNow() : TimeSpan.FromMilliseconds(1));
    }

    private bool AdmissionStillValid(OwnedAdmission admission) => admission.Phase != OwnedPhase.Cancelled &&
        !_cancelledAdmissions.ContainsKey(admission.Start.ConnectionId) &&
        admission.Start.AdmissionExpiresAtUtc > _timeProvider.GetUtcNow() && admission.Start.AuthenticationExpiresAtUtc > _timeProvider.GetUtcNow();
    private bool MatchesLocalOwner(OwnedOperation operation) => operation.Generation == _ownerGeneration && operation.Owner is { } owner &&
        _activeConnectionId == owner.ConnectionId && _activeEpoch == owner.Epoch;
    private void RemoveAdmissionIfGeneration(Guid connection, long generation)
    { if (_ownedAdmissions.TryGetValue(connection, out var admission) && admission.Generation == generation) _ownedAdmissions.Remove(connection); }
    private void RemoveCancelledAdmissionIfIdle(Guid connection)
    {
        if (_ownedAdmissions.TryGetValue(connection, out var admission) && admission.Phase == OwnedPhase.Cancelled &&
            !_ownedOperations.Values.Any(x => x.ConnectionId == connection && x.Kind is OwnedKind.Reserve or OwnedKind.Commit))
            _ownedAdmissions.Remove(connection);
    }
    private static HeartbeatRequest Heartbeat(RecordGatewayHeartbeat message, OwnerKey owner) =>
        new(owner, message.Sequence, message.ReceivedAtUtc, message.RenewedAuthenticationExpiresAtUtc);
    private static bool ValidRenewal(RecordGatewayHeartbeat message, DateTimeOffset? previous, DateTimeOffset now) =>
        message.RenewedAuthenticationExpiresAtUtc is not { } expiry || previous is { } old && expiry > old && expiry > now && expiry > message.ReceivedAtUtc;
    private static DateTimeOffset Minimum(params DateTimeOffset[] values) => values.Min();
    private static TimeSpan PositiveTimerDelay(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.FromMilliseconds(1);
    private static bool SameStart(StartGatewayPresenceSession a, StartGatewayPresenceSession b) =>
        a.Client == b.Client && a.ConnectionId == b.ConnectionId && a.OperationId == b.OperationId && a.ProtocolVersion == b.ProtocolVersion &&
        a.AgentVersion == b.AgentVersion && a.Capabilities.SequenceEqual(b.Capabilities, StringComparer.Ordinal) &&
        a.LegacySpacetimeIdentity == b.LegacySpacetimeIdentity && a.ReceivedAtUtc == b.ReceivedAtUtc &&
        a.AuthenticationExpiresAtUtc == b.AuthenticationExpiresAtUtc && a.AdmissionExpiresAtUtc == b.AdmissionExpiresAtUtc && a.ProvisionalAdmission == b.ProvisionalAdmission;
    private static void ReplyUnavailable(IActorRef reply) => reply.Tell(new Status.Failure(new InvalidOperationException("gateway-ownership-operation-unavailable")));
    private static PresenceMessageDisposition MapOwnership(OwnershipDisposition disposition) => disposition switch
    {
        OwnershipDisposition.Accepted => PresenceMessageDisposition.Accepted,
        OwnershipDisposition.Duplicate => PresenceMessageDisposition.Duplicate,
        OwnershipDisposition.AdmissionExpired => PresenceMessageDisposition.AdmissionExpired,
        OwnershipDisposition.AdmissionCancelled => PresenceMessageDisposition.AdmissionCancelled,
        OwnershipDisposition.AdmissionCapacityExceeded => PresenceMessageDisposition.AdmissionCapacityExceeded,
        OwnershipDisposition.AdmissionBodyConflict or OwnershipDisposition.ConnectionMismatch => PresenceMessageDisposition.ConnectionMismatch,
        OwnershipDisposition.StaleEpoch => PresenceMessageDisposition.StaleConnectionEpoch,
        OwnershipDisposition.AuthenticationExpired => PresenceMessageDisposition.AuthenticationExpired,
        OwnershipDisposition.InvalidRenewal => PresenceMessageDisposition.InvalidAuthenticationRenewal,
        OwnershipDisposition.StaleSequence => PresenceMessageDisposition.StaleSequence,
        _ => PresenceMessageDisposition.NoActiveSession
    };

    protected override void PostStop()
    {
        if (_ownership is not null)
        {
            foreach (var operation in _ownedOperations.Values) { operation.Cancellation.Cancel(); operation.Cancellation.Dispose(); }
            foreach (var admission in _ownedAdmissions.Values)
                ObserveStoppedCleanup(_ownership.CancelAdmissionAsync(_client, admission.Start.ConnectionId,
                    admission.Reservation?.Owner.Epoch, _timeProvider.GetUtcNow(), CancellationToken.None));
            if (_activeConnectionId is { } connection && _activeEpoch is { } epoch)
                ObserveStoppedCleanup(_ownership.RetireAsync(new(_client, connection, epoch), RetirementReason.ExplicitClose, null, CancellationToken.None));
        }
        base.PostStop();
    }

    private static void ObserveStoppedCleanup(Task cleanup) => _ = cleanup.ContinueWith(task => _ = task.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}

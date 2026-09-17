using Akka.Actor;
using System.Text.Json;
using NetRatel.Application.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Akka.RemoteSupport;

/// <summary>
/// The sole runtime writer for one Remote Support V2 lifecycle. The actor owns
/// ordering and fencing; the durable PostgreSQL projection is its recovery and
/// audit boundary. It deliberately has no signalling or media payload surface.
/// </summary>
public sealed class RemoteSupportSessionActor : ReceiveActor, IWithTimers
{
    private static readonly object ExpiryTimerKey = new();
    private static readonly object TransitionTimerKey = new();
    private RemoteSupportSessionKey? _session;
    private readonly IRemoteSupportLifecycleStore _store;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<Guid, IActorRef> _browserEdges = [];
    private readonly Dictionary<Guid, IActorRef> _browserNegotiationEdges = [];
    private readonly Dictionary<string, long> _negotiationSequences = [];
    private readonly HashSet<(string Direction, long Generation, Guid MessageId)> _negotiationMessageIds = [];
    private readonly Queue<(string Direction, long Generation, Guid MessageId)> _negotiationMessageIdOrder = [];
    private readonly RemoteSupportTargetTransitionCoordinator _transitions = new(TimeProvider.System);
    private RemoteSupportAgentEdge? _agentEdge;
    private RemoteSupportSessionSnapshot? _snapshot;
    private RemoteSupportPreparedTargetResult? _preparedTarget;
    private long _negotiationGeneration = 1;
    private bool _firstFrameObserved;
    private bool _dataChannelObserved;
    private bool _inputReadyObserved;
    private bool _loaded;

    public RemoteSupportSessionActor(
        RemoteSupportSessionKey session,
        IRemoteSupportLifecycleStore store)
        : this(store)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>Cluster-sharded constructor. The first routed envelope fences the entity identity.</summary>
    public RemoteSupportSessionActor(IRemoteSupportLifecycleStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Timers = null!;

        ReceiveAsync<GetRemoteSupportSession>(async message =>
        {
            var replyTo = Sender;
            await EnsureLoadedAsync().ConfigureAwait(false);
            replyTo.Tell(IsBoundTo(message.Operator) ? _snapshot : null);
        });
        ReceiveAsync<GetRemoteSupportSessionByKey>(async message =>
        {
            var replyTo = Sender;
            if (!TryBindSession(message.Session))
            {
                replyTo.Tell(null);
                return;
            }

            await EnsureLoadedAsync().ConfigureAwait(false);
            replyTo.Tell(IsBoundTo(message.Operator) ? _snapshot : null);
        });
        ReceiveAsync<OpenRemoteSupportSessionByKey>(async message =>
        {
            var replyTo = Sender;
            if (!TryBindSession(message.Session))
            {
                replyTo.Tell(new Status.Failure(new InvalidOperationException("The routed session identity is fenced.")));
                return;
            }

            try
            {
                var opened = await _store.OpenAsync(message.Command, _session!.RemoteSupportSessionId, _stopping.Token)
                    .ConfigureAwait(false);
                _snapshot = opened.Snapshot;
                _loaded = true;
                ScheduleExpiry();
                replyTo.Tell(opened.Snapshot);
            }
            catch (Exception exception)
            {
                replyTo.Tell(new Status.Failure(exception));
            }
        });
        ReceiveAsync<ResumeRemoteSupportSession>(async message =>
        {
            var replyTo = Sender;
            await EnsureLoadedAsync().ConfigureAwait(false);
            if (!IsBoundTo(message.Request.Operator))
            {
                replyTo.Tell(null);
                return;
            }

            var audit = await _store.ReadAuditAsync(
                    _session!,
                    message.Request.AfterAuditSequence,
                    _stopping.Token)
                .ConfigureAwait(false);
            replyTo.Tell(new RemoteSupportSessionResume(_snapshot!, audit));
        });
        ReceiveAsync<ResumeRemoteSupportSessionByKey>(async message =>
        {
            var replyTo = Sender;
            if (!TryBindSession(message.Request.Session))
            {
                replyTo.Tell(null);
                return;
            }

            await EnsureLoadedAsync().ConfigureAwait(false);
            if (!IsBoundTo(message.Request.Operator))
            {
                replyTo.Tell(null);
                return;
            }

            var audit = await _store.ReadAuditAsync(_session!, message.Request.AfterAuditSequence, _stopping.Token).ConfigureAwait(false);
            replyTo.Tell(new RemoteSupportSessionResume(_snapshot!, audit));
        });
        ReceiveAsync<ControlRemoteSupportSession>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await ControlAsync(message.Command).ConfigureAwait(false));
        });
        ReceiveAsync<ControlRemoteSupportSessionByKey>(async message =>
        {
            var replyTo = Sender;
            if (!TryBindSession(message.Command.Session))
            {
                replyTo.Tell(Rejected());
                return;
            }

            replyTo.Tell(await ControlAsync(message.Command).ConfigureAwait(false));
        });
        ReceiveAsync<AdvanceRemoteSupportSession>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await AdvanceAsync(message.Command).ConfigureAwait(false));
        });
        ReceiveAsync<AdvanceRemoteSupportSessionByKey>(async message =>
        {
            var replyTo = Sender;
            if (!TryBindSession(message.Command.Session))
            {
                replyTo.Tell(Rejected());
                return;
            }

            replyTo.Tell(await AdvanceAsync(message.Command).ConfigureAwait(false));
        });
        ReceiveAsync<PrepareRemoteSupportMedia>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await PrepareMediaAsync(message).ConfigureAwait(false));
        });
        ReceiveAsync<PrepareRemoteSupportMediaByKey>(async message =>
        {
            var replyTo = Sender;
            if (!TryBindSession(message.Command.Session))
            {
                replyTo.Tell(Rejected());
                return;
            }

            replyTo.Tell(await PrepareMediaAsync(message.Command).ConfigureAwait(false));
        });
        ReceiveAsync<StartRemoteSupportTargetTransition>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await StartTransitionAsync(message).ConfigureAwait(false));
        });
        ReceiveAsync<ObserveRemoteSupportTransitionInventory>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await ObserveTransitionInventoryAsync(message).ConfigureAwait(false));
        });
        ReceiveAsync<SelectRemoteSupportTransitionTarget>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await SelectTransitionTargetAsync(message).ConfigureAwait(false));
        });
        ReceiveAsync<GetRemoteSupportTransitionSelection>(async message =>
        {
            var replyTo = Sender;
            await EnsureLoadedAsync().ConfigureAwait(false);
            replyTo.Tell(_snapshot is not null && IsBoundTo(message.Operator) &&
                         _transitions.State == RemoteSupportTransitionState.TargetSelectionRequired && _transitions.Fence is not null
                ? new RemoteSupportTransitionSelection(_transitions.Fence, _transitions.SelectionCandidates)
                : null);
        });
        ReceiveAsync<CompleteRemoteSupportReplacementPreparation>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await CompleteReplacementPreparationAsync(message).ConfigureAwait(false));
        });
        ReceiveAsync<CompleteRemoteSupportReplacementNegotiation>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await CompleteReplacementNegotiationAsync(message).ConfigureAwait(false));
        });
        ReceiveAsync<ReceiveRemoteSupportTransitionEvidence>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await ReceiveTransitionEvidenceAsync(message).ConfigureAwait(false));
        });
        ReceiveAsync<TickRemoteSupportTargetTransition>(async _ => await TickTransitionAsync().ConfigureAwait(false));
        ReceiveAsync<RemoteSupportNegotiationIngress>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await ReceiveNegotiationAsync(message).ConfigureAwait(false));
        });
        ReceiveAsync<ReceiveRemoteSupportNegotiationByKey>(async message =>
        {
            var replyTo = Sender;
            if (!TryBindSession(message.Ingress.Envelope.Session))
            {
                replyTo.Tell(RemoteSupportNegotiationIngressResult.Rejected("session_fenced", _negotiationGeneration));
                return;
            }

            replyTo.Tell(await ReceiveNegotiationAsync(message.Ingress).ConfigureAwait(false));
        });
        ReceiveAsync<RegisterRemoteSupportBrowserEdge>(async message =>
        {
            var replyTo = Sender;
            if (!TryBindSession(message.Session))
            {
                replyTo.Tell(RemoteSupportBrowserEdgeRegistration.RejectedResult);
                return;
            }

            await EnsureLoadedAsync().ConfigureAwait(false);
            if (!IsBoundTo(message.Operator))
            {
                replyTo.Tell(RemoteSupportBrowserEdgeRegistration.RejectedResult);
                return;
            }

            _browserEdges[message.EdgeRouteId] = message.Edge;
            Context.Watch(message.Edge);
            if (_snapshot is not null && _snapshot.LifecycleRevision > message.AfterAuditSequence)
            {
                message.Edge.Tell(new RemoteSupportBrowserEdgeEvent(new(
                    _snapshot.LifecycleRevision,
                    _snapshot,
                    RemoteSupportV2AuditEventTypes.LifecycleChanged)));
            }

            replyTo.Tell(RemoteSupportBrowserEdgeRegistration.AcceptedResult);
        });
        Receive<UnregisterRemoteSupportBrowserEdge>(message =>
        {
            if (TryBindSession(message.Session) &&
                _browserEdges.Remove(message.EdgeRouteId, out var edge))
            {
                Context.Unwatch(edge);
            }
        });
        ReceiveAsync<RegisterRemoteSupportBrowserNegotiationEdge>(async message =>
        {
            var replyTo = Sender;
            if (!TryBindSession(message.Session))
            {
                replyTo.Tell(RemoteSupportBrowserEdgeRegistration.RejectedResult);
                return;
            }

            await EnsureLoadedAsync().ConfigureAwait(false);
            if (!IsBoundTo(message.Operator) || _snapshot is null || IsTerminal(_snapshot.State))
            {
                replyTo.Tell(RemoteSupportBrowserEdgeRegistration.RejectedResult);
                return;
            }

            _browserNegotiationEdges[message.EdgeRouteId] = message.Edge;
            Context.Watch(message.Edge);
            replyTo.Tell(RemoteSupportBrowserEdgeRegistration.AcceptedResult);
        });
        Receive<UnregisterRemoteSupportBrowserNegotiationEdge>(message =>
        {
            if (TryBindSession(message.Session) && _browserNegotiationEdges.Remove(message.EdgeRouteId, out var edge))
            {
                Context.Unwatch(edge);
            }
        });
        Receive<Terminated>(message =>
        {
            foreach (var route in _browserEdges.Where(entry => entry.Value.Equals(message.ActorRef)).Select(entry => entry.Key).ToArray())
            {
                _browserEdges.Remove(route);
            }

            foreach (var route in _browserNegotiationEdges.Where(entry => entry.Value.Equals(message.ActorRef)).Select(entry => entry.Key).ToArray())
            {
                _browserNegotiationEdges.Remove(route);
            }

            if (_agentEdge?.Edge.Equals(message.ActorRef) == true)
            {
                _agentEdge = null;
            }
        });
        ReceiveAsync<RegisterRemoteSupportAgentEdge>(async message =>
        {
            var replyTo = Sender;
            if (!TryBindSession(message.Session) || message.AgentId != _session!.AgentId ||
                message.ConnectionId == Guid.Empty || message.ConnectionEpoch == 0 ||
                message.EdgeRouteId == Guid.Empty || message.RouteGeneration <= 0)
            {
                replyTo.Tell(RemoteSupportAgentEdgeRegistration.RejectedResult);
                return;
            }

            await EnsureLoadedAsync().ConfigureAwait(false);
            if (_snapshot is null || IsTerminal(_snapshot.State))
            {
                replyTo.Tell(RemoteSupportAgentEdgeRegistration.RejectedResult);
                return;
            }

            if (_agentEdge is { } current && IsSameAgentEdge(current, message))
            {
                replyTo.Tell(RemoteSupportAgentEdgeRegistration.AcceptedResult);
                return;
            }

            if (_agentEdge is not null && !CanReplaceAgentEdge(_agentEdge, message))
            {
                replyTo.Tell(RemoteSupportAgentEdgeRegistration.RejectedResult);
                return;
            }

            if (_agentEdge is not null)
            {
                Context.Unwatch(_agentEdge.Edge);
            }

            _agentEdge = new RemoteSupportAgentEdge(
                message.ConnectionId,
                message.ConnectionEpoch,
                message.EdgeRouteId,
                message.RouteGeneration,
                message.Edge);
            Context.Watch(message.Edge);
            if (_preparedTarget is not null && _snapshot?.State == RemoteSupportV2SessionStates.PreparingTarget)
            {
                await PromotePreparedMediaToReadyAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
            }
            else if (_preparedTarget is not null && _snapshot?.State is RemoteSupportV2SessionStates.Negotiating or RemoteSupportV2SessionStates.Connected)
            {
                await RequireFreshNegotiationAfterEdgeRecoveryAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
            }
            message.Edge.Tell(new RemoteSupportAgentRouteEnvelope(
                _session!,
                message.EdgeRouteId,
                message.RouteGeneration,
                "renegotiation_required",
                [],
                new RemoteSupportV2NegotiationEnvelope(
                    _session!,
                    _negotiationGeneration,
                    RemoteSupportV2NegotiationDirections.Agent,
                    1,
                    Guid.NewGuid(),
                    RemoteSupportV2NegotiationSignalTypes.Status,
                    "{\"code\":\"renegotiation_required\"}"u8.ToArray())));
            DispatchPendingTransitionEffect();
            replyTo.Tell(RemoteSupportAgentEdgeRegistration.AcceptedResult);
        });
        Receive<UnregisterRemoteSupportAgentEdge>(message =>
        {
            if (TryBindSession(message.Session) && _agentEdge is { } edge &&
                edge.EdgeRouteId == message.EdgeRouteId && edge.ConnectionEpoch == message.ConnectionEpoch)
            {
                Context.Unwatch(edge.Edge);
                _agentEdge = null;
            }
        });
        Receive<RouteRemoteSupportAgentEnvelope>(message =>
        {
            if (TryBindSession(message.Envelope.Session) && _agentEdge is { } edge &&
                edge.EdgeRouteId == message.Envelope.EdgeRouteId &&
                edge.RouteGeneration == message.Envelope.RouteGeneration)
            {
                edge.Edge.Tell(message.Envelope);
            }
        });
        ReceiveAsync<ExpireRemoteSupportSession>(async _ =>
        {
            await EnsureLoadedAsync().ConfigureAwait(false);
            if (_snapshot is not null && !IsTerminal(_snapshot.State) &&
                _snapshot.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= DateTimeOffset.UtcNow)
            {
                await ApplyAsync(
                        RemoteSupportV2SessionStates.Expired,
                        expectedRevision: _snapshot.LifecycleRevision,
                        RemoteSupportV2AuditEventTypes.SessionExpired,
                        "system",
                        "remote-support-expiry",
                        Guid.NewGuid(),
                        "expired",
                        "session_expired",
                        DateTimeOffset.UtcNow)
                    .ConfigureAwait(false);
            }
        });
    }

    public ITimerScheduler Timers { get; set; }

    public static Props Props(RemoteSupportSessionKey session, IRemoteSupportLifecycleStore store) =>
        global::Akka.Actor.Props.Create(() => new RemoteSupportSessionActor(session, store));

    public static Props Props(IRemoteSupportLifecycleStore store) =>
        global::Akka.Actor.Props.Create(() => new RemoteSupportSessionActor(store));

    protected override void PostStop()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        base.PostStop();
    }

    private async Task<RemoteSupportTransitionDecision> StartTransitionAsync(StartRemoteSupportTargetTransition command)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (command.Session != _session || _snapshot is null || _preparedTarget?.HelperRoute is null ||
            IsTerminal(_snapshot.State) || command.TransitionSequence == 0)
        {
            return TransitionRejected("transition_session_fenced");
        }

        var decision = _transitions.Start(
            _session!, _preparedTarget.Target, _preparedTarget.HelperRoute.HelperRouteId,
            _negotiationGeneration, command.PresenceEpoch, command.TransitionSequence,
            command.Reason, command.ActiveConsoleMatched);
        if (!decision.Accepted)
        {
            return decision;
        }

        ResetReadinessProof();
        await ApplyAsync(
                RemoteSupportV2SessionStates.RenegotiationRequired,
                _snapshot.LifecycleRevision,
                RemoteSupportV2AuditEventTypes.TransitionStarted,
                "agent",
                _session.AgentId.ToString("D"),
                command.RequestId,
                "control_suspended",
                null,
                command.OccurredAtUtc)
            .ConfigureAwait(false);
        DispatchTransitionEffects(decision);
        ScheduleTransitionTick();
        return decision;
    }

    private async Task<RemoteSupportTransitionDecision> ObserveTransitionInventoryAsync(ObserveRemoteSupportTransitionInventory command)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (command.Session != _session || _snapshot is null || IsTerminal(_snapshot.State) ||
            !RemoteSupportV2PreparationValidator.TryValidate(command.Inventory, out _))
        {
            return TransitionRejected("transition_inventory_invalid");
        }

        var decision = _transitions.ObserveInventory(command.Inventory, command.TransitionId, command.PresenceEpoch);
        if (decision.Accepted && decision.Code == "target_selection_required")
        {
            await ApplyAsync(
                    _snapshot.State,
                    _snapshot.LifecycleRevision,
                    RemoteSupportV2AuditEventTypes.TargetSelectionRequired,
                    "system",
                    "remote-support-transition",
                    command.RequestId,
                    "selection_required",
                    null,
                    command.ObservedAtUtc)
                .ConfigureAwait(false);
        }

        DispatchTransitionEffects(decision);
        ScheduleTransitionTick();

        return decision;
    }

    private async Task<RemoteSupportTransitionDecision> CompleteReplacementPreparationAsync(CompleteRemoteSupportReplacementPreparation command)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (command.Session != _session || _snapshot is null || IsTerminal(_snapshot.State) ||
            !RemoteSupportV2PreparationValidator.TryValidate(command.PreparedTarget, out _))
        {
            return TransitionRejected("replacement_preparation_invalid");
        }

        var decision = _transitions.ReplacementPrepared(command.TransitionId, command.PresenceEpoch, command.PreparedTarget);
        if (!decision.Accepted || decision.FreshGeneration is null)
        {
            return decision;
        }

        _preparedTarget = command.PreparedTarget;
        _negotiationGeneration = decision.FreshGeneration.Value;
        ResetReadinessProof();
        var applied = await ApplyAsync(
                RemoteSupportV2SessionStates.RenegotiationRequired,
                _snapshot.LifecycleRevision,
                RemoteSupportV2AuditEventTypes.ReplacementPrepared,
                "agent",
                _session!.AgentId.ToString("D"),
                command.RequestId,
                "fresh_generation_required",
                null,
                command.ObservedAtUtc)
            .ConfigureAwait(false);
        if (applied.Disposition is RemoteSupportLifecycleTransitionDisposition.Applied or RemoteSupportLifecycleTransitionDisposition.Duplicate &&
            _agentEdge is not null)
        {
            await PromotePreparedMediaToReadyAsync(command.ObservedAtUtc).ConfigureAwait(false);
        }
        return decision;
    }

    private async Task<RemoteSupportTransitionDecision> SelectTransitionTargetAsync(SelectRemoteSupportTransitionTarget command)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (command.Session != _session || _snapshot is null || IsTerminal(_snapshot.State) ||
            !IsBoundTo(command.Operator) || _snapshot.LifecycleRevision != command.ExpectedLifecycleRevision)
        {
            return TransitionRejected("target_selection_unauthorized");
        }

        var decision = _transitions.Select(
            command.TransitionId,
            command.PresenceEpoch,
            command.InventorySequence,
            command.WindowsSessionId,
            command.IdentityReference);
        DispatchTransitionEffects(decision);
        ScheduleTransitionTick();
        return decision;
    }

    private async Task<RemoteSupportTransitionDecision> CompleteReplacementNegotiationAsync(CompleteRemoteSupportReplacementNegotiation command)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (command.Session != _session || _snapshot is null || IsTerminal(_snapshot.State) ||
            _snapshot.State != RemoteSupportV2SessionStates.Connected)
        {
            return TransitionRejected("replacement_negotiation_invalid");
        }

        var decision = _transitions.CompleteNegotiation(command.TransitionId, command.PresenceEpoch, command.Generation);
        if (decision.Accepted)
        {
            await ApplyAsync(
                    _snapshot.State,
                    _snapshot.LifecycleRevision,
                    RemoteSupportV2AuditEventTypes.TransitionCompleted,
                    "system",
                    "remote-support-transition",
                    command.RequestId,
                    "replacement_negotiation_complete",
                    null,
                    command.OccurredAtUtc)
                .ConfigureAwait(false);
            ScheduleTransitionTick();
        }

        return decision;
    }

    private RemoteSupportTransitionDecision TransitionRejected(string code) =>
        new(false, code, _transitions.State, _transitions.Fence, false, false, false, null, null, null, null, []);

    private async Task TickTransitionAsync()
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (_snapshot is null || IsTerminal(_snapshot.State))
        {
            return;
        }

        var decision = _transitions.Tick();
        if (decision.FailureCode is not null)
        {
            await ApplyAsync(
                    RemoteSupportV2SessionStates.Failed,
                    _snapshot.LifecycleRevision,
                    RemoteSupportV2AuditEventTypes.LifecycleChanged,
                    "system",
                    "remote-support-transition",
                    Guid.NewGuid(),
                    "failed",
                    decision.FailureCode,
                    DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
        }
        else
        {
            ScheduleTransitionTick();
        }
    }

    private void DispatchPendingTransitionEffect()
    {
        var fence = _transitions.Fence;
        if (fence is null)
        {
            return;
        }

        var decision = _transitions.State switch
        {
            RemoteSupportTransitionState.AwaitingInventory or RemoteSupportTransitionState.ConvergingInventory =>
                new RemoteSupportTransitionDecision(true, "transition_effect_replay", _transitions.State, fence, true, false, false, null, null, null, null, []),
            RemoteSupportTransitionState.PreparingReplacementTarget when fence.ReplacementTarget is not null =>
                new RemoteSupportTransitionDecision(true, "transition_effect_replay", _transitions.State, fence, false, false, false, null, fence.ReplacementTarget, null, null, []),
            _ => TransitionRejected("transition_effect_not_pending")
        };
        DispatchTransitionEffects(decision);
    }

    private void DispatchTransitionEffects(RemoteSupportTransitionDecision decision)
    {
        if (!decision.Accepted || decision.Fence is null || _agentEdge is null || _snapshot is null)
        {
            return;
        }

        if (decision.RequestInventory)
        {
            DispatchTransitionEffect(decision.Fence, RemoteSupportTransitionEffectKinds.ReacquireInventory, null);
        }

        if (decision.SelectedTarget is not null)
        {
            DispatchTransitionEffect(decision.Fence, RemoteSupportTransitionEffectKinds.PrepareReplacementTarget, decision.SelectedTarget);
        }

        if (decision.CancelPending)
        {
            DispatchTransitionEffect(decision.Fence, RemoteSupportTransitionEffectKinds.Cancel, null);
        }
    }

    private void DispatchTransitionEffect(
        RemoteSupportTransitionFence fence,
        string kind,
        RemoteSupportTargetDescriptor? target)
    {
        if (_agentEdge is null || _snapshot is null)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new RemoteSupportTransitionEffect(
                Guid.NewGuid(),
                fence.Session,
                fence.TransitionId,
                fence.PresenceEpoch,
                _negotiationGeneration,
                kind,
                _snapshot.InitiatingOperator,
                target),
            RemoteSupportV2JsonContext.Default.RemoteSupportTransitionEffect);
        _agentEdge.Edge.Tell(new RemoteSupportAgentRouteEnvelope(
            fence.Session,
            _agentEdge.EdgeRouteId,
            _agentEdge.RouteGeneration,
            "v2_transition_effect",
            payload));
    }

    private async Task<bool> ReceiveTransitionEvidenceAsync(ReceiveRemoteSupportTransitionEvidence message)
    {
        var evidence = message.Evidence;
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (evidence.Session != _session || _snapshot is null || _preparedTarget?.HelperRoute is null ||
            IsTerminal(_snapshot.State) || !RemoteSupportV2ContractValidator.TryValidate(evidence, out _) ||
            _agentEdge is null || _agentEdge.EdgeRouteId != message.AgentEdgeRouteId ||
            _agentEdge.RouteGeneration != message.AgentRouteGeneration || evidence.NegotiationGeneration != _negotiationGeneration ||
            (evidence.HelperRouteId is { } routeId && routeId != _preparedTarget.HelperRoute.HelperRouteId))
        {
            return false;
        }

        var activeConsoleMatched = !RemoteSupportV2TargetKinds.IsConsoleLogin(_preparedTarget.Target.Kind) ||
            evidence.ActiveConsoleSessionId is null || evidence.ActiveConsoleSessionId == _preparedTarget.HelperRoute.WindowsSessionId;
        var reason = EvidenceReason(evidence.EvidenceKind);
        if (reason is null)
        {
            return false;
        }

        var decision = await StartTransitionAsync(new StartRemoteSupportTargetTransition(
                evidence.Session,
                evidence.EvidenceId,
                message.PresenceEpoch,
                evidence.TransitionSequence,
                reason.Value,
                activeConsoleMatched,
                evidence.ObservedAtUtc))
            .ConfigureAwait(false);
        return decision.Accepted;
    }

    private static RemoteSupportTransitionReason? EvidenceReason(string evidenceKind) => evidenceKind switch
    {
        RemoteSupportV2TransitionEvidenceKinds.WorkstationLocked => RemoteSupportTransitionReason.LockDetected,
        RemoteSupportV2TransitionEvidenceKinds.WorkstationUnlocked or
        RemoteSupportV2TransitionEvidenceKinds.SignedInDesktopObserved or
        RemoteSupportV2TransitionEvidenceKinds.WinlogonVisible => RemoteSupportTransitionReason.ConsoleLoginDetected,
        RemoteSupportV2TransitionEvidenceKinds.SessionLoggedOff => RemoteSupportTransitionReason.LogoffDetected,
        RemoteSupportV2TransitionEvidenceKinds.SessionDisconnected => RemoteSupportTransitionReason.RdpDisconnected,
        RemoteSupportV2TransitionEvidenceKinds.HelperDisconnected or
        RemoteSupportV2TransitionEvidenceKinds.ProviderLost or
        RemoteSupportV2TransitionEvidenceKinds.DesktopRecoveryExhausted => RemoteSupportTransitionReason.DesktopRecoveryExhausted,
        RemoteSupportV2TransitionEvidenceKinds.CaptureFailedAfterFirstFrame => RemoteSupportTransitionReason.CaptureFailureAfterFrame,
        RemoteSupportV2TransitionEvidenceKinds.ActiveConsoleChanged => RemoteSupportTransitionReason.ConsoleLoginDetected,
        _ => null
    };

    private async Task<RemoteSupportLifecycleTransitionResult> ControlAsync(RemoteSupportControlCommand command)
    {
        if (!RemoteSupportV2ContractValidator.TryValidate(command, out _) || command.Session != _session)
        {
            return Rejected();
        }

        await EnsureLoadedAsync().ConfigureAwait(false);
        if (!IsBoundTo(command.Operator) || _snapshot is null || IsTerminal(_snapshot.State))
        {
            return Rejected();
        }

        return command.ControlType.Trim().ToLowerInvariant() switch
        {
            RemoteSupportV2ControlTypes.Close => await CloseAsync(command).ConfigureAwait(false),
            RemoteSupportV2ControlTypes.RequestResume => await ApplyAsync(
                    RemoteSupportV2SessionStates.RenegotiationRequired,
                    command.ExpectedLifecycleRevision,
                    RemoteSupportV2AuditEventTypes.ControlRequested,
                    "operator",
                    command.Operator.OperatorId,
                    command.RequestId,
                    "accepted",
                    "resume_requested",
                    command.RequestedAtUtc)
                .ConfigureAwait(false),
            RemoteSupportV2ControlTypes.RequestSas or RemoteSupportV2ControlTypes.RepairHelper =>
                await DispatchPrivilegedControlAsync(command).ConfigureAwait(false),
            _ => Rejected()
        };
    }

    private async Task<RemoteSupportLifecycleTransitionResult> CloseAsync(RemoteSupportControlCommand command)
    {
        var result = await ApplyAsync(
            RemoteSupportV2SessionStates.Completed,
            command.ExpectedLifecycleRevision,
            RemoteSupportV2AuditEventTypes.SessionClosed,
            "operator",
            command.Operator.OperatorId,
            command.RequestId,
            "accepted",
            "operator_closed",
            command.RequestedAtUtc).ConfigureAwait(false);
        if (result.Disposition == RemoteSupportLifecycleTransitionDisposition.Applied)
        {
            DispatchTransitionEffects(_transitions.Close());
            ScheduleTransitionTick();
        }
        return result;
    }

    private async Task<RemoteSupportLifecycleTransitionResult> DispatchPrivilegedControlAsync(RemoteSupportControlCommand command)
    {
        if (_transitions.IsControlSuspended || _snapshot is null || _preparedTarget?.HelperRoute is null || _agentEdge is null ||
            (_snapshot.State is not RemoteSupportV2SessionStates.ReadyForOffer and
                not RemoteSupportV2SessionStates.Negotiating and
                not RemoteSupportV2SessionStates.Connected))
        {
            return Rejected();
        }

        if (command.ControlType == RemoteSupportV2ControlTypes.RequestSas &&
            !RemoteSupportV2TargetKinds.IsConsoleLogin(_preparedTarget.Target.Kind))
        {
            return Rejected();
        }

        var applied = await ApplyAsync(
                _snapshot.State,
                command.ExpectedLifecycleRevision,
                RemoteSupportV2AuditEventTypes.ControlRequested,
                "operator",
                command.Operator.OperatorId,
                command.RequestId,
                "dispatched",
                null,
                command.RequestedAtUtc)
            .ConfigureAwait(false);
        if (applied.Disposition != RemoteSupportLifecycleTransitionDisposition.Applied || applied.Snapshot is null)
        {
            return applied;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new RemoteSupportV2PrivilegedControl(
                _session!,
                command.RequestId,
                command.ControlType,
                applied.Snapshot.LifecycleRevision,
                _negotiationGeneration,
                _preparedTarget.Target,
                _preparedTarget.HelperRoute.HelperRouteId),
            RemoteSupportV2JsonContext.Default.RemoteSupportV2PrivilegedControl);
        _agentEdge.Edge.Tell(new RemoteSupportAgentRouteEnvelope(
            _session!,
            _agentEdge.EdgeRouteId,
            _agentEdge.RouteGeneration,
            "v2_control",
            payload));
        return applied;
    }

    private async Task<RemoteSupportLifecycleTransitionResult> AdvanceAsync(AdvanceRemoteSupportSessionLifecycle command)
    {
        if (command.ContractVersion != RemoteSupportV2ContractVersions.Current ||
            command.Session != _session || command.RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.NextState))
        {
            return Rejected();
        }

        await EnsureLoadedAsync().ConfigureAwait(false);
        if (_snapshot is null || IsTerminal(_snapshot.State) ||
            !IsValidTransition(_snapshot.State, command.NextState))
        {
            return Rejected();
        }

        return await ApplyAsync(
                command.NextState,
                command.ExpectedLifecycleRevision,
                RemoteSupportV2AuditEventTypes.LifecycleChanged,
                "agent",
                _session.AgentId.ToString("D"),
                command.RequestId,
                "accepted",
                null,
                command.OccurredAtUtc)
            .ConfigureAwait(false);
    }

    private async Task<RemoteSupportLifecycleTransitionResult> PrepareMediaAsync(PrepareRemoteSupportMedia command)
    {
        if (command.Session != _session || command.RequestId == Guid.Empty ||
            !RemoteSupportV2PreparationValidator.TryValidate(command.PreparedTarget, out _) ||
            (!string.Equals(command.PreparedTarget.Target.Kind, RemoteSupportV2TargetKinds.InteractiveUser, StringComparison.OrdinalIgnoreCase) &&
             !RemoteSupportV2TargetKinds.IsConsoleLogin(command.PreparedTarget.Target.Kind)))
        {
            return Rejected();
        }

        await EnsureLoadedAsync().ConfigureAwait(false);
        if (!IsBoundTo(command.Operator) || _snapshot is null || command.PreparedTarget.Target != _snapshot.Target ||
            _snapshot.State is not RemoteSupportV2SessionStates.Requested and not RemoteSupportV2SessionStates.PreparingTarget ||
            !command.PreparedTarget.TargetValid || !command.PreparedTarget.ProviderReady || command.PreparedTarget.HelperRoute is null)
        {
            return Rejected();
        }

        // Replacements in preparing_target need the same durable revision and
        // request-id fences as the initial preparation, before changing its route.
        var preparing = await ApplyAsync(
                    RemoteSupportV2SessionStates.PreparingTarget,
                    command.ExpectedLifecycleRevision,
                    RemoteSupportV2AuditEventTypes.LifecycleChanged,
                    "operator",
                    command.Operator.OperatorId,
                    command.RequestId,
                    "accepted",
                    null,
                    command.RequestedAtUtc)
                .ConfigureAwait(false);
        if (preparing.Disposition != RemoteSupportLifecycleTransitionDisposition.Applied ||
            _snapshot is null || _snapshot.State != RemoteSupportV2SessionStates.PreparingTarget)
        {
            return preparing;
        }

        _preparedTarget = command.PreparedTarget;
        _negotiationGeneration = Math.Max(1, _negotiationGeneration);
        ResetReadinessProof();
        return _agentEdge is null
            ? preparing
            : await PromotePreparedMediaToReadyAsync(command.RequestedAtUtc).ConfigureAwait(false);
    }

    private Task<RemoteSupportLifecycleTransitionResult> PromotePreparedMediaToReadyAsync(DateTimeOffset occurredAtUtc)
    {
        if (_snapshot is null || _preparedTarget is null || _agentEdge is null ||
            _snapshot.State is not RemoteSupportV2SessionStates.PreparingTarget and not RemoteSupportV2SessionStates.RenegotiationRequired)
        {
            return Task.FromResult(Rejected());
        }

        return ApplyAsync(
                RemoteSupportV2SessionStates.ReadyForOffer,
                _snapshot.LifecycleRevision,
                RemoteSupportV2AuditEventTypes.LifecycleChanged,
                "agent",
                _session!.AgentId.ToString("D"),
                Guid.NewGuid(),
                "ready",
                null,
                occurredAtUtc);
    }

    private async Task RequireFreshNegotiationAfterEdgeRecoveryAsync(DateTimeOffset occurredAtUtc)
    {
        if (_snapshot is null || _preparedTarget is null || _agentEdge is null)
        {
            return;
        }

        _negotiationGeneration = checked(_negotiationGeneration + 1);
        ResetReadinessProof();
        var reconnecting = await ApplyAsync(
                RemoteSupportV2SessionStates.Reconnecting,
                _snapshot.LifecycleRevision,
                RemoteSupportV2AuditEventTypes.LifecycleChanged,
                "system",
                "remote-support-edge-recovery",
                Guid.NewGuid(),
                "reconnecting",
                "agent_edge_replaced",
                occurredAtUtc)
            .ConfigureAwait(false);
        if (reconnecting.Snapshot is null)
        {
            return;
        }

        var renegotiating = await ApplyAsync(
                RemoteSupportV2SessionStates.RenegotiationRequired,
                reconnecting.Snapshot.LifecycleRevision,
                RemoteSupportV2AuditEventTypes.LifecycleChanged,
                "system",
                "remote-support-edge-recovery",
                Guid.NewGuid(),
                "renegotiation_required",
                "agent_edge_replaced",
                occurredAtUtc)
            .ConfigureAwait(false);
        if (renegotiating.Snapshot is not null)
        {
            await PromotePreparedMediaToReadyAsync(occurredAtUtc).ConfigureAwait(false);
        }
    }

    private async Task<RemoteSupportNegotiationIngressResult> ReceiveNegotiationAsync(RemoteSupportNegotiationIngress ingress)
    {
        var envelope = ingress.Envelope;
        if (envelope.Session != _session || !RemoteSupportV2ContractValidator.TryValidate(envelope, out _))
        {
            return RemoteSupportNegotiationIngressResult.Rejected("negotiation_envelope_invalid", _negotiationGeneration);
        }

        await EnsureLoadedAsync().ConfigureAwait(false);
        if (_snapshot is null || IsTerminal(_snapshot.State) || _transitions.IsNegotiationSuspended || envelope.Generation != _negotiationGeneration)
        {
            return RemoteSupportNegotiationIngressResult.Rejected("negotiation_generation_stale", _negotiationGeneration);
        }

        if (envelope.Direction == RemoteSupportV2NegotiationDirections.Browser)
        {
            if (ingress.BrowserOperator is null || !IsBoundTo(ingress.BrowserOperator))
            {
                return RemoteSupportNegotiationIngressResult.Rejected("browser_operator_fenced", _negotiationGeneration);
            }
        }
        else if (_agentEdge is null || ingress.AgentEdgeRouteId != _agentEdge.EdgeRouteId ||
                 ingress.AgentRouteGeneration != _agentEdge.RouteGeneration)
        {
            return RemoteSupportNegotiationIngressResult.Rejected("agent_edge_fenced", _negotiationGeneration);
        }

        var sequenceKey = $"{envelope.Direction}:{envelope.Generation}";
        if (_negotiationSequences.TryGetValue(sequenceKey, out var lastSequence) && envelope.Sequence <= lastSequence)
        {
            return RemoteSupportNegotiationIngressResult.Rejected("negotiation_sequence_stale", _negotiationGeneration);
        }

        var messageKey = (envelope.Direction, envelope.Generation, envelope.MessageId);
        if (!_negotiationMessageIds.Add(messageKey))
        {
            return RemoteSupportNegotiationIngressResult.Rejected("negotiation_message_duplicate", _negotiationGeneration);
        }

        _negotiationMessageIdOrder.Enqueue(messageKey);
        if (_negotiationMessageIdOrder.Count > 128)
        {
            _negotiationMessageIds.Remove(_negotiationMessageIdOrder.Dequeue());
        }

        _negotiationSequences[sequenceKey] = envelope.Sequence;
        if (envelope.SignalType == RemoteSupportV2NegotiationSignalTypes.Offer)
        {
            if (_snapshot.State != RemoteSupportV2SessionStates.ReadyForOffer || _preparedTarget?.ProviderReady != true || _agentEdge is null)
            {
                return RemoteSupportNegotiationIngressResult.Rejected("ready_for_offer_required", _negotiationGeneration);
            }

            var negotiating = await ApplyAsync(
                    RemoteSupportV2SessionStates.Negotiating,
                    _snapshot.LifecycleRevision,
                    RemoteSupportV2AuditEventTypes.LifecycleChanged,
                    "operator",
                    ingress.BrowserOperator!.OperatorId,
                    Guid.NewGuid(),
                    "accepted",
                    null,
                    DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
            if (negotiating.Disposition is not RemoteSupportLifecycleTransitionDisposition.Applied and not RemoteSupportLifecycleTransitionDisposition.Duplicate)
            {
                return RemoteSupportNegotiationIngressResult.Rejected("negotiation_transition_rejected", _negotiationGeneration);
            }

            _agentEdge.Edge.Tell(new RemoteSupportAgentRouteEnvelope(
                envelope.Session, _agentEdge.EdgeRouteId, _agentEdge.RouteGeneration, "v2_negotiation", [], envelope));
            return RemoteSupportNegotiationIngressResult.AcceptedResult(_negotiationGeneration);
        }

        if (_snapshot.State != RemoteSupportV2SessionStates.Negotiating && _snapshot.State != RemoteSupportV2SessionStates.Connected)
        {
            return RemoteSupportNegotiationIngressResult.Rejected("negotiation_not_active", _negotiationGeneration);
        }

        if (envelope.SignalType == RemoteSupportV2NegotiationSignalTypes.Status && !TryRecordReadiness(envelope))
        {
            return RemoteSupportNegotiationIngressResult.Rejected("negotiation_status_invalid", _negotiationGeneration);
        }

        if (envelope.Direction == RemoteSupportV2NegotiationDirections.Agent)
        {
            PublishBrowserNegotiation(envelope);
        }

        if (_snapshot.State == RemoteSupportV2SessionStates.Negotiating && _firstFrameObserved && _dataChannelObserved && _inputReadyObserved)
        {
            await ApplyAsync(
                    RemoteSupportV2SessionStates.Connected,
                    _snapshot.LifecycleRevision,
                    RemoteSupportV2AuditEventTypes.LifecycleChanged,
                    "system",
                    "remote-support-media-readiness",
                    Guid.NewGuid(),
                    "connected",
                    null,
                    DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
        }

        return RemoteSupportNegotiationIngressResult.AcceptedResult(_negotiationGeneration);
    }

    private bool TryRecordReadiness(RemoteSupportV2NegotiationEnvelope envelope)
    {
        try
        {
            using var document = JsonDocument.Parse(envelope.Payload);
            if (!document.RootElement.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            switch (codeElement.GetString())
            {
                case "first_frame" when envelope.Direction == RemoteSupportV2NegotiationDirections.Browser:
                    _firstFrameObserved = true;
                    return true;
                case "data_channel_open" when envelope.Direction == RemoteSupportV2NegotiationDirections.Browser:
                    _dataChannelObserved = true;
                    return true;
                case "input_ready" when envelope.Direction == RemoteSupportV2NegotiationDirections.Agent:
                    _inputReadyObserved = true;
                    return true;
                case "helper_route_invalid" or "helper_disconnected" or "offer_rejected" or "input_provider_unavailable"
                    when envelope.Direction == RemoteSupportV2NegotiationDirections.Agent:
                    return true;
                case { } code when envelope.Direction == RemoteSupportV2NegotiationDirections.Agent &&
                    (code.StartsWith("sas_", StringComparison.Ordinal) ||
                     code.StartsWith("helper_repair_", StringComparison.Ordinal) ||
                     code.StartsWith("control_", StringComparison.Ordinal)):
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ResetReadinessProof()
    {
        _firstFrameObserved = false;
        _dataChannelObserved = false;
        _inputReadyObserved = false;
        _negotiationSequences.Clear();
        _negotiationMessageIds.Clear();
        _negotiationMessageIdOrder.Clear();
    }

    private async Task<RemoteSupportLifecycleTransitionResult> ApplyAsync(
        string nextState,
        long? expectedRevision,
        string eventType,
        string actorKind,
        string actorId,
        Guid requestId,
        string outcome,
        string? failureCode,
        DateTimeOffset occurredAtUtc)
    {
        if (_snapshot is null || !IsValidTransition(_snapshot.State, nextState))
        {
            return Rejected();
        }

        var result = await _store.TransitionAsync(
                new RemoteSupportLifecycleTransition(
            _session!,
                    expectedRevision,
                    nextState,
                    eventType,
                    actorKind,
                    actorId,
                    requestId,
                    outcome,
                    failureCode,
                    occurredAtUtc),
                _stopping.Token)
            .ConfigureAwait(false);
        if (result.Snapshot is not null)
        {
            _snapshot = result.Snapshot;
            ScheduleExpiry();
            if (result.Audit is not null)
            {
                PublishBrowserLifecycle(result.Audit);
            }
        }

        return result;
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        if (_session is null)
        {
            return;
        }

        _snapshot = await _store.LoadAsync(_session, _stopping.Token).ConfigureAwait(false);
        _loaded = true;
        if (_snapshot is null)
        {
            return;
        }

        // Actor memory is intentionally non-durable. Any active lifecycle
        // rehydrated after an actor/process loss must renegotiate; this does
        // not claim that SDP, ICE, or an existing media path survived.
        if (!IsTerminal(_snapshot.State) &&
            _snapshot.State is not RemoteSupportV2SessionStates.Requested and not RemoteSupportV2SessionStates.RenegotiationRequired)
        {
            var recovery = await ApplyAsync(
                    RemoteSupportV2SessionStates.RenegotiationRequired,
                    _snapshot.LifecycleRevision,
                    RemoteSupportV2AuditEventTypes.LifecycleChanged,
                    "system",
                    "remote-support-recovery",
                    Guid.NewGuid(),
                    "recovered",
                    "renegotiation_required_after_recovery",
                    DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
            if (recovery.Snapshot is not null)
            {
                _snapshot = recovery.Snapshot;
            }
        }

        ScheduleExpiry();
    }

    private void ScheduleExpiry()
    {
        // IWithTimers is injected by the Akka runtime. Guarding the local test
        // construction path keeps recovery logic independent of timer setup.
        if (Timers is null)
        {
            return;
        }

        if (_snapshot?.ExpiresAtUtc is not { } expiresAtUtc || IsTerminal(_snapshot.State))
        {
            Timers.Cancel(ExpiryTimerKey);
            return;
        }

        var delay = expiresAtUtc - DateTimeOffset.UtcNow;
        Timers.StartSingleTimer(ExpiryTimerKey, new ExpireRemoteSupportSession(),
            delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
    }

    private void ScheduleTransitionTick()
    {
        if (Timers is null)
        {
            return;
        }

        if (_transitions.State is RemoteSupportTransitionState.Stable or RemoteSupportTransitionState.Closed or RemoteSupportTransitionState.Failed)
        {
            Timers.Cancel(TransitionTimerKey);
            return;
        }

        Timers.StartSingleTimer(TransitionTimerKey, new TickRemoteSupportTargetTransition(), TimeSpan.FromSeconds(1));
    }

    private bool IsBoundTo(RemoteSupportOperatorBinding operatorBinding) =>
        _snapshot is not null &&
        string.Equals(_snapshot.InitiatingOperator.OperatorId, operatorBinding.OperatorId, StringComparison.Ordinal);

    private bool TryBindSession(RemoteSupportSessionKey session)
    {
        if (_session is null)
        {
            _session = session;
            return true;
        }

        return _session == session;
    }

    private RemoteSupportLifecycleTransitionResult Rejected() =>
        new(RemoteSupportLifecycleTransitionDisposition.Rejected, _snapshot, null);

    private void PublishBrowserLifecycle(RemoteSupportAuditEvent audit)
    {
        if (_snapshot is null)
        {
            return;
        }

        var lifecycleEvent = new RemoteSupportBrowserLifecycleEvent(
            audit.AuditSequence,
            _snapshot,
            audit.EventType);
        foreach (var edge in _browserEdges.Values)
        {
            edge.Tell(new RemoteSupportBrowserEdgeEvent(lifecycleEvent));
        }
    }

    private void PublishBrowserNegotiation(RemoteSupportV2NegotiationEnvelope envelope)
    {
        foreach (var edge in _browserNegotiationEdges.Values)
        {
            edge.Tell(new RemoteSupportBrowserNegotiationEdgeEvent(envelope));
        }
    }

    private static bool CanReplaceAgentEdge(RemoteSupportAgentEdge current, RegisterRemoteSupportAgentEdge next) =>
        next.ConnectionEpoch > current.ConnectionEpoch ||
        (next.ConnectionEpoch == current.ConnectionEpoch &&
         next.RouteGeneration > current.RouteGeneration);

    private static bool IsSameAgentEdge(RemoteSupportAgentEdge current, RegisterRemoteSupportAgentEdge next) =>
        current.ConnectionId == next.ConnectionId &&
        current.ConnectionEpoch == next.ConnectionEpoch &&
        current.EdgeRouteId == next.EdgeRouteId &&
        current.RouteGeneration == next.RouteGeneration &&
        current.Edge.Equals(next.Edge);

    internal static bool IsTerminal(string state) => state is
        RemoteSupportV2SessionStates.Completed or
        RemoteSupportV2SessionStates.Failed or
        RemoteSupportV2SessionStates.Expired;

    internal static bool IsValidTransition(string current, string next) =>
        current == next || (current, next) switch
        {
            (RemoteSupportV2SessionStates.Requested, RemoteSupportV2SessionStates.PreparingTarget) => true,
            (RemoteSupportV2SessionStates.PreparingTarget, RemoteSupportV2SessionStates.ReadyForOffer) => true,
            (RemoteSupportV2SessionStates.ReadyForOffer, RemoteSupportV2SessionStates.Negotiating) => true,
            (RemoteSupportV2SessionStates.Negotiating, RemoteSupportV2SessionStates.Connected) => true,
            (RemoteSupportV2SessionStates.Negotiating, RemoteSupportV2SessionStates.Reconnecting) => true,
            (RemoteSupportV2SessionStates.Connected, RemoteSupportV2SessionStates.Reconnecting) => true,
            (RemoteSupportV2SessionStates.Reconnecting, RemoteSupportV2SessionStates.RenegotiationRequired) => true,
            (RemoteSupportV2SessionStates.RenegotiationRequired, RemoteSupportV2SessionStates.Negotiating) => true,
            (RemoteSupportV2SessionStates.RenegotiationRequired, RemoteSupportV2SessionStates.ReadyForOffer) => true,
            (_, RemoteSupportV2SessionStates.Completed or RemoteSupportV2SessionStates.Failed or RemoteSupportV2SessionStates.Expired) => true,
            (_, RemoteSupportV2SessionStates.RenegotiationRequired) => true,
            _ => false
        };
}

public sealed record GetRemoteSupportSession(RemoteSupportOperatorBinding Operator);
public sealed record GetRemoteSupportTransitionSelectionByKey(RemoteSupportSessionKey Session, RemoteSupportOperatorBinding Operator) : IRemoteSupportSessionEnvelope;
public sealed record ResumeRemoteSupportSession(RemoteSupportResumeRequest Request);
public sealed record ControlRemoteSupportSession(RemoteSupportControlCommand Command);
public sealed record AdvanceRemoteSupportSession(AdvanceRemoteSupportSessionLifecycle Command);
public sealed record ExpireRemoteSupportSession;
/// <summary>Agent-reported Windows evidence; no input acknowledgement is a transition prerequisite.</summary>
public sealed record StartRemoteSupportTargetTransition(
    RemoteSupportSessionKey Session,
    Guid RequestId,
    ulong PresenceEpoch,
    ulong TransitionSequence,
    RemoteSupportTransitionReason Reason,
    bool ActiveConsoleMatched,
    DateTimeOffset OccurredAtUtc) : IRemoteSupportSessionEnvelope;
internal sealed record TickRemoteSupportTargetTransition;
/// <summary>One complete, agent-authenticated WTS inventory observation for the current transition.</summary>
public sealed record ObserveRemoteSupportTransitionInventory(
    RemoteSupportSessionKey Session,
    Guid RequestId,
    Guid TransitionId,
    ulong PresenceEpoch,
    RemoteSupportTargetInventorySnapshot Inventory,
    DateTimeOffset ObservedAtUtc) : IRemoteSupportSessionEnvelope;
/// <summary>Operator choice for a current multi-target transition; stale picker results are rejected.</summary>
public sealed record SelectRemoteSupportTransitionTarget(
    RemoteSupportSessionKey Session,
    RemoteSupportOperatorBinding Operator,
    Guid RequestId,
    Guid TransitionId,
    ulong PresenceEpoch,
    ulong InventorySequence,
    int WindowsSessionId,
    string IdentityReference,
    long ExpectedLifecycleRevision) : IRemoteSupportSessionEnvelope;
/// <summary>Current operator-bound replacement picker; identity references are SID hashes only.</summary>
public sealed record GetRemoteSupportTransitionSelection(RemoteSupportOperatorBinding Operator);
public sealed record RemoteSupportTransitionSelection(RemoteSupportTransitionFence Fence, IReadOnlyList<RemoteSupportTransitionCandidate> Candidates);
/// <summary>Exact replacement proof returned by the agent after actor-selected target preparation.</summary>
public sealed record CompleteRemoteSupportReplacementPreparation(
    RemoteSupportSessionKey Session,
    Guid RequestId,
    Guid TransitionId,
    ulong PresenceEpoch,
    RemoteSupportPreparedTargetResult PreparedTarget,
    DateTimeOffset ObservedAtUtc) : IRemoteSupportSessionEnvelope;
/// <summary>Final proof that the replacement peer belongs to the actor-issued fresh generation.</summary>
public sealed record CompleteRemoteSupportReplacementNegotiation(
    RemoteSupportSessionKey Session,
    Guid RequestId,
    Guid TransitionId,
    ulong PresenceEpoch,
    long Generation,
    DateTimeOffset OccurredAtUtc) : IRemoteSupportSessionEnvelope;
/// <summary>Authenticated edge ingress for a factual, current-generation endpoint observation.</summary>
public sealed record ReceiveRemoteSupportTransitionEvidence(
    RemoteSupportV2TransitionEvidence Evidence,
    Guid AgentEdgeRouteId,
    long AgentRouteGeneration,
    ulong PresenceEpoch) : IRemoteSupportSessionEnvelope
{
    public RemoteSupportSessionKey Session => Evidence.Session;
}
public sealed record RegisterRemoteSupportBrowserEdge(
    RemoteSupportSessionKey Session,
    RemoteSupportOperatorBinding Operator,
    Guid EdgeRouteId,
    long AfterAuditSequence,
    IActorRef Edge) : IRemoteSupportSessionEnvelope;
public sealed record UnregisterRemoteSupportBrowserEdge(
    RemoteSupportSessionKey Session,
    Guid EdgeRouteId) : IRemoteSupportSessionEnvelope;
public sealed record RegisterRemoteSupportBrowserNegotiationEdge(
    RemoteSupportSessionKey Session,
    RemoteSupportOperatorBinding Operator,
    Guid EdgeRouteId,
    IActorRef Edge) : IRemoteSupportSessionEnvelope;
public sealed record UnregisterRemoteSupportBrowserNegotiationEdge(
    RemoteSupportSessionKey Session,
    Guid EdgeRouteId) : IRemoteSupportSessionEnvelope;
public sealed record RemoteSupportBrowserEdgeRegistration(bool Accepted)
{
    public static readonly RemoteSupportBrowserEdgeRegistration AcceptedResult = new(true);
    public static readonly RemoteSupportBrowserEdgeRegistration RejectedResult = new(false);
}
public sealed record RegisterRemoteSupportAgentEdge(
    RemoteSupportSessionKey Session,
    Guid AgentId,
    Guid ConnectionId,
    ulong ConnectionEpoch,
    Guid EdgeRouteId,
    long RouteGeneration,
    IActorRef Edge) : IRemoteSupportSessionEnvelope;
public sealed record UnregisterRemoteSupportAgentEdge(
    RemoteSupportSessionKey Session,
    Guid EdgeRouteId,
    ulong ConnectionEpoch) : IRemoteSupportSessionEnvelope;
public sealed record RouteRemoteSupportAgentEnvelope(RemoteSupportAgentRouteEnvelope Envelope) : IRemoteSupportSessionEnvelope
{
    public RemoteSupportSessionKey Session => Envelope.Session;
}
public sealed record RemoteSupportAgentEdgeRegistration(bool Accepted)
{
    public static readonly RemoteSupportAgentEdgeRegistration AcceptedResult = new(true);
    public static readonly RemoteSupportAgentEdgeRegistration RejectedResult = new(false);
}

internal sealed record RemoteSupportAgentEdge(
    Guid ConnectionId,
    ulong ConnectionEpoch,
    Guid EdgeRouteId,
    long RouteGeneration,
    IActorRef Edge);

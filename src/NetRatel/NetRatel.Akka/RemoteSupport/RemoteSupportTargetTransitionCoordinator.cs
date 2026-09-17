using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Akka.RemoteSupport;

/// <summary>
/// Pure, actor-owned reducer for Windows desktop/provider changes.  Windows
/// observations are intentionally kept out of the durable lifecycle snapshot:
/// they are short-lived proof used to fence a replacement media child.
/// </summary>
public sealed class RemoteSupportTargetTransitionCoordinator(TimeProvider timeProvider, RemoteSupportTransitionOptions? options = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly RemoteSupportTransitionOptions _options = options ?? RemoteSupportTransitionOptions.Default;
    private RemoteSupportTransitionState _state = RemoteSupportTransitionState.Stable;
    private RemoteSupportTransitionFence? _fence;
    private string? _candidateFingerprint;
    private int _equivalentObservations;
    private DateTimeOffset _deadline;
    private bool _repairIssued;
    private IReadOnlyList<RemoteSupportTransitionCandidate> _selectionCandidates = [];

    public RemoteSupportTransitionState State => _state;
    public RemoteSupportTransitionFence? Fence => _fence;
    public IReadOnlyList<RemoteSupportTransitionCandidate> SelectionCandidates => _selectionCandidates;
    public bool IsControlSuspended => _state is not RemoteSupportTransitionState.Stable and not RemoteSupportTransitionState.Closed;
    public bool IsNegotiationSuspended => _state is not RemoteSupportTransitionState.Stable and not RemoteSupportTransitionState.RenegotiationRequired;

    public RemoteSupportTransitionDecision Start(
        RemoteSupportSessionKey session,
        RemoteSupportTargetDescriptor currentTarget,
        Guid? currentProviderRouteId,
        long mediaGeneration,
        ulong presenceEpoch,
        ulong transitionSequence,
        RemoteSupportTransitionReason reason,
        bool activeConsoleMatched)
    {
        if (_state is not RemoteSupportTransitionState.Stable || session.RemoteSupportSessionId == Guid.Empty || mediaGeneration <= 0 || transitionSequence == 0 ||
            (RemoteSupportV2TargetKinds.IsConsoleLogin(currentTarget.Kind) && !activeConsoleMatched))
        {
            return Reject("transition_evidence_invalid");
        }

        var now = _timeProvider.GetUtcNow();
        _fence = new RemoteSupportTransitionFence(
            session, currentTarget, currentProviderRouteId, mediaGeneration, presenceEpoch,
            transitionSequence, Guid.NewGuid(), reason, now);
        _state = RemoteSupportTransitionState.AwaitingInventory;
        _candidateFingerprint = null;
        _equivalentObservations = 0;
        _repairIssued = false;
        _selectionCandidates = [];
        _deadline = now.Add(_options.InventoryWait);
        return Accept("transition_started", requestInventory: true);
    }

    public RemoteSupportTransitionDecision ObserveInventory(
        RemoteSupportTargetInventorySnapshot inventory,
        Guid transitionId,
        ulong presenceEpoch)
    {
        if (!IsCurrent(transitionId, presenceEpoch) || _state is RemoteSupportTransitionState.Closed or RemoteSupportTransitionState.Failed)
        {
            return Reject("transition_inventory_stale");
        }

        if (_state is not RemoteSupportTransitionState.AwaitingInventory and not RemoteSupportTransitionState.ConvergingInventory)
        {
            return Reject("transition_inventory_unexpected");
        }

        if (inventory.InventorySequence <= _fence!.LastInventorySequence ||
            inventory.TenantId != _fence.Session.TenantId || inventory.AgentId != _fence.Session.AgentId)
        {
            return Reject("inventory_sequence_stale");
        }

        _fence = _fence with { LastInventorySequence = inventory.InventorySequence };
        _state = RemoteSupportTransitionState.ConvergingInventory;
        _deadline = _timeProvider.GetUtcNow().Add(_options.Convergence);
        var candidates = ToCandidates(inventory);
        var fingerprint = string.Join('|', candidates.Select(candidate => $"{candidate.WindowsSessionId}:{candidate.IdentityReference}"));
        _equivalentObservations = string.Equals(_candidateFingerprint, fingerprint, StringComparison.Ordinal)
            ? _equivalentObservations + 1
            : 1;
        _candidateFingerprint = fingerprint;
        if (_equivalentObservations < 2)
        {
            return Accept("converging_inventory", requestInventory: true, candidates: candidates);
        }

        if (candidates.Count == 0)
        {
            _state = RemoteSupportTransitionState.AwaitingInventory;
            _deadline = _timeProvider.GetUtcNow().Add(_options.InventoryWait);
            return Accept("waiting_for_signed_in_target", requestInventory: true);
        }

        var automatic = CanAutomaticallySelect(candidates[0], candidates.Count);
        if (automatic)
        {
            return SelectCore(candidates[0], inventory.InventorySequence, automatic: true);
        }

        _state = RemoteSupportTransitionState.TargetSelectionRequired;
        _selectionCandidates = candidates;
        _deadline = _timeProvider.GetUtcNow().Add(_options.SelectionWait);
        return Accept("target_selection_required", candidates: candidates);
    }

    public RemoteSupportTransitionDecision Select(
        Guid transitionId,
        ulong presenceEpoch,
        ulong inventorySequence,
        int windowsSessionId,
        string? identityReference)
    {
        if (!IsCurrent(transitionId, presenceEpoch) || _state != RemoteSupportTransitionState.TargetSelectionRequired ||
            inventorySequence != _fence!.LastInventorySequence)
        {
            return Reject("target_selection_stale");
        }

        if (windowsSessionId <= 0 || string.IsNullOrWhiteSpace(identityReference))
        {
            return Reject("target_selection_invalid");
        }

        var candidate = _selectionCandidates.SingleOrDefault(candidate =>
            candidate.WindowsSessionId == windowsSessionId &&
            string.Equals(candidate.IdentityReference, identityReference, StringComparison.Ordinal));
        return candidate is null
            ? Reject("target_selection_not_current")
            : SelectCore(candidate, inventorySequence, automatic: false);
    }

    public RemoteSupportTransitionDecision ReplacementPrepared(
        Guid transitionId,
        ulong presenceEpoch,
        RemoteSupportPreparedTargetResult prepared)
    {
        if (!IsCurrent(transitionId, presenceEpoch) || _state != RemoteSupportTransitionState.PreparingReplacementTarget ||
            _fence!.ReplacementTarget is null || prepared.Target != _fence.ReplacementTarget ||
            prepared.InventorySequence != _fence.LastInventorySequence || !prepared.TargetValid || !prepared.ProviderReady || prepared.HelperRoute is null)
        {
            return Reject("replacement_preparation_stale");
        }

        _fence = _fence with
        {
            ReplacementProviderRouteId = prepared.HelperRoute.HelperRouteId,
            ReplacementGeneration = checked(_fence.MediaGeneration + 1)
        };
        _state = RemoteSupportTransitionState.RenegotiationRequired;
        _deadline = _timeProvider.GetUtcNow().Add(_options.Negotiation);
        return Accept("replacement_prepared", replacement: prepared, freshGeneration: _fence.ReplacementGeneration);
    }

    public RemoteSupportTransitionDecision CompleteNegotiation(Guid transitionId, ulong presenceEpoch, long generation)
    {
        if (!IsCurrent(transitionId, presenceEpoch) || _state != RemoteSupportTransitionState.RenegotiationRequired ||
            generation != _fence!.ReplacementGeneration)
        {
            return Reject("replacement_negotiation_stale");
        }

        _state = RemoteSupportTransitionState.Stable;
        return Accept("transition_completed", freshGeneration: generation);
    }

    public RemoteSupportTransitionDecision RequestExactRepair(Guid transitionId, ulong presenceEpoch)
    {
        if (!IsCurrent(transitionId, presenceEpoch) || _state != RemoteSupportTransitionState.PreparingReplacementTarget || _repairIssued)
        {
            return Reject("helper_repair_stale_or_duplicate");
        }

        _repairIssued = true;
        return Accept("helper_repair_requested", requestRepair: true);
    }

    public RemoteSupportTransitionDecision PresenceChanged(ulong presenceEpoch)
    {
        if (_fence is null || presenceEpoch <= _fence.PresenceEpoch || _state == RemoteSupportTransitionState.Closed)
        {
            return Reject("presence_epoch_stale");
        }

        _fence = _fence with { PresenceEpoch = presenceEpoch, ReplacementTarget = null, ReplacementProviderRouteId = null, ReplacementGeneration = null };
        _state = RemoteSupportTransitionState.AwaitingInventory;
        _candidateFingerprint = null;
        _equivalentObservations = 0;
        _selectionCandidates = [];
        _deadline = _timeProvider.GetUtcNow().Add(_options.InventoryWait);
        return Accept("presence_changed_reacquire", requestInventory: true);
    }

    public RemoteSupportTransitionDecision Tick()
    {
        if (_state is RemoteSupportTransitionState.Stable or RemoteSupportTransitionState.Closed or RemoteSupportTransitionState.Failed ||
            _timeProvider.GetUtcNow() < _deadline)
        {
            return Accept("transition_waiting");
        }

        _state = RemoteSupportTransitionState.Failed;
        return Accept("transition_failed", failureCode: "transition_timeout");
    }

    public RemoteSupportTransitionDecision Close()
    {
        _state = RemoteSupportTransitionState.Closed;
        return Accept("transition_closed", cancelPending: true);
    }

    private RemoteSupportTransitionDecision SelectCore(RemoteSupportTransitionCandidate candidate, ulong inventorySequence, bool automatic)
    {
        _fence = _fence! with
        {
            ReplacementTarget = new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.InteractiveUser, candidate.WindowsSessionId, candidate.IdentityReference, inventorySequence)
        };
        _state = RemoteSupportTransitionState.PreparingReplacementTarget;
        _deadline = _timeProvider.GetUtcNow().Add(_options.Preparation);
        return Accept(automatic ? "target_automatically_selected" : "target_selected", target: _fence.ReplacementTarget);
    }

    private bool CanAutomaticallySelect(RemoteSupportTransitionCandidate candidate, int count) =>
        count == 1 && _fence is not null && RemoteSupportV2TargetKinds.IsConsoleLogin(_fence.CurrentTarget.Kind) &&
        candidate.IsAssistable && candidate.WindowsSessionId > 0;

    private static IReadOnlyList<RemoteSupportTransitionCandidate> ToCandidates(RemoteSupportTargetInventorySnapshot inventory) =>
        inventory.Entries
            .Where(entry => entry.WindowsSessionId > 0 && entry.IsConnected && !entry.IsLocked && !entry.IsWinlogon &&
                            entry.HelperConnected && entry.HelperVersionMatches && !string.IsNullOrWhiteSpace(entry.UserSidHash))
            .OrderBy(entry => entry.WindowsSessionId)
            .Select(entry => new RemoteSupportTransitionCandidate(entry.WindowsSessionId, entry.UserSidHash!, entry.State, entry.IsConsoleSession, true))
            .ToArray();

    private bool IsCurrent(Guid transitionId, ulong presenceEpoch) => _fence is not null && _fence.TransitionId == transitionId && _fence.PresenceEpoch == presenceEpoch;
    private RemoteSupportTransitionDecision Accept(string code, bool requestInventory = false, bool requestRepair = false, bool cancelPending = false, string? failureCode = null, RemoteSupportTargetDescriptor? target = null, RemoteSupportPreparedTargetResult? replacement = null, long? freshGeneration = null, IReadOnlyList<RemoteSupportTransitionCandidate>? candidates = null) =>
        new(true, code, _state, _fence, requestInventory, requestRepair, cancelPending, failureCode, target, replacement, freshGeneration, candidates ?? []);
    private RemoteSupportTransitionDecision Reject(string code) => new(false, code, _state, _fence, false, false, false, null, null, null, null, []);
}

public sealed record RemoteSupportTransitionOptions(TimeSpan InventoryWait, TimeSpan Convergence, TimeSpan SelectionWait, TimeSpan Preparation, TimeSpan Negotiation)
{
    public static RemoteSupportTransitionOptions Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
}

public enum RemoteSupportTransitionState { Stable, TransitionDetected, ControlSuspended, AwaitingInventory, ConvergingInventory, TargetSelectionRequired, PreparingReplacementTarget, ReplacementReady, RenegotiationRequired, Failed, Closed }
public enum RemoteSupportTransitionReason { ConsoleLoginDetected, LockDetected, UnlockDetected, LogoffDetected, RdpDisconnected, DesktopRecoveryExhausted, CaptureFailureAfterFrame }

public sealed record RemoteSupportTransitionFence(RemoteSupportSessionKey Session, RemoteSupportTargetDescriptor CurrentTarget, Guid? CurrentProviderRouteId, long MediaGeneration, ulong PresenceEpoch, ulong TransitionSequence, Guid TransitionId, RemoteSupportTransitionReason Reason, DateTimeOffset StartedAtUtc, ulong LastInventorySequence = 0, RemoteSupportTargetDescriptor? ReplacementTarget = null, Guid? ReplacementProviderRouteId = null, long? ReplacementGeneration = null);
public sealed record RemoteSupportTransitionCandidate(int WindowsSessionId, string IdentityReference, string State, bool IsConsole, bool IsAssistable);
public sealed record RemoteSupportTransitionDecision(bool Accepted, string Code, RemoteSupportTransitionState State, RemoteSupportTransitionFence? Fence, bool RequestInventory, bool RequestRepair, bool CancelPending, string? FailureCode, RemoteSupportTargetDescriptor? SelectedTarget, RemoteSupportPreparedTargetResult? Replacement, long? FreshGeneration, IReadOnlyList<RemoteSupportTransitionCandidate> Candidates);

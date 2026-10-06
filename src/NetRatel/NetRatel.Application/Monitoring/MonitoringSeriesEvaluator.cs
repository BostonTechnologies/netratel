using System.Collections.Immutable;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.Application.Monitoring;

/// <summary>Pure per-series decisions. Persistence owns serialization, uniqueness, authorization and dispatch leases.</summary>
public sealed class MonitoringSeriesEvaluator(TimeProvider timeProvider, Func<Guid>? nextId = null)
{
    private readonly Func<Guid> _nextId = nextId ?? Guid.NewGuid;

    public MonitoringSeriesState CreateInitial(MonitoringSeriesKey series, MonitoringRuleDto rule)
    {
        MonitoringContractValidator.RequireRule(rule);
        if (!MatchesRule(series, rule)) throw new ArgumentException("invalid_series");
        return new(series, 0, rule.EvaluationRevision, MonitoringPhase.Healthy, MonitoringEvidenceQuality.Unknown,
            NotBeforeObservedAtUtc: timeProvider.GetUtcNow(), NotBeforeReceivedAtUtc: timeProvider.GetUtcNow(),
            ApplicableBypassIds: [], EvaluationFingerprint: MonitoringContractValidator.EvaluationFingerprint(rule));
    }

    public MonitoringEvaluationResult Evaluate(MonitoringSeriesState state, MonitoringRuleDto rule, MonitoringObservationDto observation,
        long currentAdmittedConnectionEpoch, Guid currentAdmittedEvidenceStreamId, IReadOnlyList<MonitoringBypassDto> bypasses, bool targetApplicable = true, IReadOnlyList<MonitoringGroupDto>? groups = null)
    {
        RequireDefinition(state, rule);
        if (observation.Series != state.Series || !MatchesRule(observation.Series, rule)) return Unchanged(state, MonitoringEvaluationDisposition.WrongSeries);
        if (observation.EvidenceStreamId == Guid.Empty || observation.EvidenceStreamId != currentAdmittedEvidenceStreamId ||
            observation.Cursor.ConnectionEpoch <= 0 || observation.Cursor.ConnectionEpoch != currentAdmittedConnectionEpoch || observation.Cursor.Sequence == 0 ||
            !IsNewCursor(observation.Cursor, state.Cursor)) return Unchanged(state, MonitoringEvaluationDisposition.DuplicateOrStaleCursor);
        if (!rule.Enabled || !targetApplicable)
            return Suspend(state, rule, rule.Enabled ? MonitoringClosureDisposition.TargetRemoved : MonitoringClosureDisposition.RuleDisabled, null, null);

        var now = timeProvider.GetUtcNow();
        var isDisk = IsDisk(rule);
        var collectionReason = isDisk ? DiskCollectionReason(state, observation, now) : null;
        if (isDisk && observation.Complete && observation.Supported && collectionReason is null &&
            state.LastDiskCollection == observation.DiskCollection)
            return EvaluateCachedDiskCollection(state, rule, observation, bypasses, now, groups);
        var evidence = Classify(state, rule, observation, now, collectionReason);
        var changedEpoch = state.Cursor is { } previousCursor && previousCursor.ConnectionEpoch != observation.Cursor.ConnectionEpoch;
        var changedStream = state.EvidenceStreamId is { } previousStream && previousStream != observation.EvidenceStreamId;
        var gap = state.LatestEvidence is { } previous &&
            (observation.ReceivedAtUtc - previous.ReceivedAtUtc > rule.FreshnessBudget || observation.ObservedAtUtc - previous.ObservedAtUtc > rule.FreshnessBudget);
        var working = state with { StateRevision = checked(state.StateRevision + 1), Cursor = observation.Cursor, EvidenceStreamId = observation.EvidenceStreamId,
            LatestEvidence = evidence, EvidenceQuality = evidence.Quality };
        // A newly committed epoch starts its own receipt clock. Invalid first input must not carry the prior epoch's receipt high water forward.
        if (isDisk && changedEpoch) working = working with { LastDiskTransportReceivedAtUtc = null };
        // Remember immutable physical collections even when their complete/precision/fence
        // classification is Unknown. Never regress this marker on malformed/reordered input.
        if (isDisk && collectionReason is null) working = working with { LastDiskCollection = observation.DiskCollection };
        if (isDisk && observation.ReceivedAtUtc != default && observation.ReceivedAtUtc <= now &&
            (PreviousDiskTransportReceipt(state, observation, now) is not { } previousReceipt || observation.ReceivedAtUtc > previousReceipt))
            working = working with { LastDiskTransportReceivedAtUtc = observation.ReceivedAtUtc };
        if (changedEpoch || changedStream || gap) working = InterruptContinuity(working);
        working = WithSuppression(working, bypasses, now, groups);
        var events = ImmutableArray.CreateBuilder<MonitoringEventIntent>();
        var outbox = ImmutableArray.CreateBuilder<MonitoringOutboxIntent>();
        if (evidence.Quality == MonitoringEvidenceQuality.Unknown)
        {
            working = InterruptContinuity(working);
            return Result(state, working, events.ToImmutable(), []);
        }

        if (!HasOpenOccurrence(working))
        {
            if (evidence.Classification == MonitoringClassification.Breach)
            {
                var continuing = working.Phase == MonitoringPhase.Pending && working.WindowStartedAtUtc is not null &&
                    (!isDisk || working.WindowStartedObservedAtUtc is not null);
                var start = continuing ? working.WindowStartedAtUtc!.Value : observation.ReceivedAtUtc;
                var observedStart = isDisk ? continuing ? working.WindowStartedObservedAtUtc!.Value : observation.ObservedAtUtc : (DateTimeOffset?)null;
                working = working with { Phase = MonitoringPhase.Pending, WindowStartedAtUtc = start,
                    WindowStartedObservedAtUtc = observedStart, PreviousQualifyingReceivedAtUtc = observation.ReceivedAtUtc };
                if (observation.ReceivedAtUtc - start >= rule.BreachHold &&
                    (!isDisk || observation.ObservedAtUtc - observedStart!.Value >= rule.BreachHold))
                {
                    var occurrence = new MonitoringOccurrenceDto(_nextId(), _nextId(), now, start, rule, evidence,
                        rule.PublishedFlowVersionId is null ? MonitoringFlowDispatchDisposition.NoFlowSelected : MonitoringFlowDispatchDisposition.Suppressed);
                    working = working with { Phase = MonitoringPhase.Firing, Occurrence = occurrence, WindowStartedAtUtc = null, WindowStartedObservedAtUtc = null, PreviousQualifyingReceivedAtUtc = null };
                    events.Add(new(occurrence.RaisedEventId, MonitoringEventKind.AlertRaised, state.Series, occurrence.OccurrenceId, now, occurrence.PinnedRule, evidence));
                }
            }
            else working = working with { Phase = MonitoringPhase.Healthy, WindowStartedAtUtc = null, WindowStartedObservedAtUtc = null, PreviousQualifyingReceivedAtUtc = null };
        }
        else if (evidence.Classification == MonitoringClassification.Recovery)
        {
            var continuing = working.Phase == MonitoringPhase.Recovering && working.WindowStartedAtUtc is not null &&
                (!isDisk || working.WindowStartedObservedAtUtc is not null);
            var start = continuing ? working.WindowStartedAtUtc!.Value : observation.ReceivedAtUtc;
            var observedStart = isDisk ? continuing ? working.WindowStartedObservedAtUtc!.Value : observation.ObservedAtUtc : (DateTimeOffset?)null;
            working = working with { Phase = MonitoringPhase.Recovering, WindowStartedAtUtc = start,
                WindowStartedObservedAtUtc = observedStart, PreviousQualifyingReceivedAtUtc = observation.ReceivedAtUtc };
            if (observation.ReceivedAtUtc - start >= rule.RecoveryHold &&
                (!isDisk || observation.ObservedAtUtc - observedStart!.Value >= rule.RecoveryHold))
            {
                var occurrence = working.Occurrence! with { EndedAtUtc = now, ClosureDisposition = MonitoringClosureDisposition.Recovered };
                working = working with { Phase = MonitoringPhase.Resolved, Occurrence = occurrence, WindowStartedAtUtc = null, WindowStartedObservedAtUtc = null, PreviousQualifyingReceivedAtUtc = null };
                events.Add(new(_nextId(), MonitoringEventKind.AlertResolved, state.Series, occurrence.OccurrenceId, now, occurrence.PinnedRule, evidence, MonitoringClosureDisposition.Recovered));
            }
        }
        else working = working with { Phase = MonitoringPhase.Firing, WindowStartedAtUtc = null, WindowStartedObservedAtUtc = null, PreviousQualifyingReceivedAtUtc = null };

        working = AddDispatchIfEligible(working, now, rule, outbox);
        return Result(state, working, events.ToImmutable(), outbox.ToImmutable());
    }

    /// <summary>Time may invalidate freshness or release a suppressed existing event; it never completes a hold.</summary>
    public MonitoringEvaluationResult Refresh(MonitoringSeriesState state, MonitoringRuleDto rule, IReadOnlyList<MonitoringBypassDto> bypasses, Guid? currentAdmittedEvidenceStreamId, IReadOnlyList<MonitoringGroupDto>? groups = null)
    {
        RequireDefinition(state, rule);
        var now = timeProvider.GetUtcNow();
        var working = WithSuppression(state, bypasses, now, groups);
        var unavailableStream = currentAdmittedEvidenceStreamId is null || currentAdmittedEvidenceStreamId == Guid.Empty || currentAdmittedEvidenceStreamId != working.EvidenceStreamId;
        if (unavailableStream || !IsCurrentFresh(working.LatestEvidence, rule, now))
        {
            working = InterruptContinuity(working) with
            {
                EvidenceQuality = MonitoringEvidenceQuality.Unknown,
                EvidenceStreamId = currentAdmittedEvidenceStreamId,
                LatestEvidence = working.LatestEvidence is { } previous ? previous with { Quality = MonitoringEvidenceQuality.Unknown,
                    Classification = MonitoringClassification.Unknown, UnknownReason = unavailableStream ? "stream_unavailable" : "stale" } : null
            };
        }
        var outbox = ImmutableArray.CreateBuilder<MonitoringOutboxIntent>();
        working = AddDispatchIfEligible(working, now, rule, outbox);
        if (working == state && outbox.Count == 0) return Unchanged(state);
        working = working with { StateRevision = checked(state.StateRevision + 1) };
        return Result(state, working, [], outbox.ToImmutable());
    }

    public MonitoringEvaluationResult ResumeAfterRestart(MonitoringSeriesState state, MonitoringRuleDto rule)
    {
        RequireDefinition(state, rule);
        var working = InterruptContinuity(state) with
        {
            StateRevision = checked(state.StateRevision + 1), EvidenceQuality = MonitoringEvidenceQuality.Unknown,
            LatestEvidence = state.LatestEvidence is { } previous ? previous with { Quality = MonitoringEvidenceQuality.Unknown, Classification = MonitoringClassification.Unknown, UnknownReason = "restart" } : null
        };
        return Result(state, working, [], []);
    }

    public MonitoringEvaluationResult BeginEvidenceStream(MonitoringSeriesState state, MonitoringRuleDto rule, Guid evidenceStreamId)
    {
        RequireDefinition(state, rule);
        if (evidenceStreamId == Guid.Empty) throw new ArgumentException("evidence_stream_required");
        if (state.EvidenceStreamId == evidenceStreamId) return Unchanged(state);
        var working = InterruptContinuity(state) with
        {
            StateRevision = checked(state.StateRevision + 1), EvidenceStreamId = evidenceStreamId, EvidenceQuality = MonitoringEvidenceQuality.Unknown,
            LatestEvidence = state.LatestEvidence is { } previous ? previous with { Quality = MonitoringEvidenceQuality.Unknown,
                Classification = MonitoringClassification.Unknown, UnknownReason = "stream_changed" } : null
        };
        return Result(state, working, [], []);
    }

    public MonitoringEvaluationResult Acknowledge(MonitoringSeriesState state, MonitoringRuleDto rule, Guid operatorId)
    {
        RequireDefinition(state, rule);
        if (operatorId == Guid.Empty) throw new ArgumentException("operator_required");
        if (!HasOpenOccurrence(state)) throw new InvalidOperationException("active_occurrence_required");
        if (state.Occurrence!.AcknowledgedAtUtc is not null) return Unchanged(state);
        var now = timeProvider.GetUtcNow();
        var working = state with { StateRevision = checked(state.StateRevision + 1), Occurrence = state.Occurrence with { AcknowledgedBy = operatorId, AcknowledgedAtUtc = now } };
        return Result(state, working, [], [], [Audit(state, rule, operatorId, "acknowledge", "acknowledged", now)]);
    }

    public MonitoringEvaluationResult Clear(MonitoringSeriesState state, MonitoringRuleDto rule, Guid operatorId, string reason)
    {
        RequireDefinition(state, rule);
        MonitoringContractValidator.RequireReason(operatorId, reason);
        if (!HasOpenOccurrence(state)) throw new InvalidOperationException("active_occurrence_required");
        var now = timeProvider.GetUtcNow();
        var occurrence = state.Occurrence! with { EndedAtUtc = now, ClosureDisposition = MonitoringClosureDisposition.ManuallyCleared };
        var working = state with { StateRevision = checked(state.StateRevision + 1), Phase = MonitoringPhase.Cleared, Occurrence = occurrence,
            WindowStartedAtUtc = null, WindowStartedObservedAtUtc = null, PreviousQualifyingReceivedAtUtc = null, EvidenceQuality = MonitoringEvidenceQuality.Unknown,
            NotBeforeObservedAtUtc = now, NotBeforeReceivedAtUtc = now };
        return Result(state, working,
            [new(_nextId(), MonitoringEventKind.AlertCleared, state.Series, occurrence.OccurrenceId, now, occurrence.PinnedRule, state.LatestEvidence!, MonitoringClosureDisposition.ManuallyCleared, reason)], [],
            [Audit(state, rule, operatorId, "clear", reason, now)]);
    }

    public MonitoringEvaluationResult SetApplicability(MonitoringSeriesState state, MonitoringRuleDto rule, bool applicable, Guid operatorId, string reason)
    {
        RequireDefinition(state, rule);
        MonitoringContractValidator.RequireReason(operatorId, reason);
        if (!rule.Enabled || !applicable) return Suspend(state, rule, !rule.Enabled ? MonitoringClosureDisposition.RuleDisabled : MonitoringClosureDisposition.TargetRemoved, operatorId, reason);
        var now = timeProvider.GetUtcNow();
        var working = state with { StateRevision = checked(state.StateRevision + 1), Phase = MonitoringPhase.Healthy, EvidenceQuality = MonitoringEvidenceQuality.Unknown,
            WindowStartedAtUtc = null, WindowStartedObservedAtUtc = null, PreviousQualifyingReceivedAtUtc = null, NotBeforeObservedAtUtc = now, NotBeforeReceivedAtUtc = now };
        return Result(state, working, [], [], [Audit(state, rule, operatorId, "resume", reason, now)]);
    }

    /// <summary>Authoritative directory changes carry no invented human principal.</summary>
    public MonitoringEvaluationResult SetAuthoritativeApplicability(MonitoringSeriesState state, MonitoringRuleDto rule, bool applicable)
    {
        RequireDefinition(state, rule);
        if (!rule.Enabled || !applicable) return Suspend(state, rule,
            !rule.Enabled ? MonitoringClosureDisposition.RuleDisabled : MonitoringClosureDisposition.TargetRemoved, null, null);
        if (state.Phase is not (MonitoringPhase.Suspended or MonitoringPhase.NotApplicable)) return Unchanged(state);
        var now = timeProvider.GetUtcNow();
        var working = state with { StateRevision = checked(state.StateRevision + 1), Phase = MonitoringPhase.Healthy,
            EvidenceQuality = MonitoringEvidenceQuality.Unknown, WindowStartedAtUtc = null, WindowStartedObservedAtUtc = null, PreviousQualifyingReceivedAtUtc = null,
            NotBeforeObservedAtUtc = now, NotBeforeReceivedAtUtc = now };
        return Result(state, working, [], []);
    }

    public MonitoringEvaluationResult ResetDefinition(MonitoringSeriesState state, MonitoringRuleDto updatedRule,
        MonitoringConditionResetPolicy policy, Guid operatorId, string reason)
    {
        MonitoringContractValidator.RequireRule(updatedRule);
        MonitoringContractValidator.RequireReason(operatorId, reason);
        if (state.Series.TenantId != updatedRule.TenantId || state.Series.RuleId != updatedRule.RuleId || updatedRule.EvaluationRevision <= state.EvaluationRevision ||
            policy != MonitoringConditionResetPolicy.SuspendOccurrenceAndRequireNewWindow) throw new ArgumentException("explicit_new_definition_reset_required");
        var suspended = Suspend(state, updatedRule, MonitoringClosureDisposition.ConfigurationChanged, operatorId, reason);
        var working = suspended.State with { EvaluationRevision = updatedRule.EvaluationRevision,
            EvaluationFingerprint = MonitoringContractValidator.EvaluationFingerprint(updatedRule),
            Phase = !updatedRule.Enabled ? MonitoringPhase.Suspended : MatchesRule(state.Series, updatedRule) ? MonitoringPhase.Healthy : MonitoringPhase.NotApplicable };
        return suspended with { State = working };
    }

    public MonitoringEvaluationResult AuditBypass(MonitoringSeriesState state, MonitoringRuleDto rule, MonitoringBypassDto bypass, bool remove)
    {
        RequireDefinition(state, rule);
        if (!MonitoringContractValidator.TryValidateBypass(bypass, out _) || bypass.TenantId != state.Series.TenantId) throw new ArgumentException("invalid_or_foreign_bypass");
        var audit = new MonitoringAuditIntent(_nextId(), state.Series, remove ? "remove_bypass" : "create_bypass", bypass.OperatorId, bypass.Reason,
            timeProvider.GetUtcNow(), state.Occurrence?.OccurrenceId, rule.Revision, bypass.BypassId);
        return Result(state, state with { StateRevision = checked(state.StateRevision + 1) }, [], [], [audit]);
    }

    /// <summary>A later dispatcher must load current state+overlays and recheck this before claiming an unstarted action.</summary>
    public bool CanDispatch(MonitoringSeriesState state, MonitoringRuleDto currentRule, MonitoringOutboxIntent intent,
        Guid currentAdmittedEvidenceStreamId, IReadOnlyList<MonitoringBypassDto> bypasses, IReadOnlyList<MonitoringGroupDto>? groups = null)
    {
        var now = timeProvider.GetUtcNow();
        if (currentAdmittedEvidenceStreamId == Guid.Empty || currentAdmittedEvidenceStreamId != state.EvidenceStreamId ||
            state.LatestEvidence?.EvidenceStreamId != currentAdmittedEvidenceStreamId || !currentRule.Enabled || currentRule.EvaluationRevision != state.EvaluationRevision ||
            MonitoringContractValidator.EvaluationFingerprint(currentRule) != state.EvaluationFingerprint || state.Series != intent.Series ||
            !HasOpenOccurrence(state) || state.Occurrence!.OccurrenceId != intent.OccurrenceId || state.Occurrence.RaisedEventId != intent.EventId ||
            state.Occurrence.FlowDispatchDisposition != MonitoringFlowDispatchDisposition.Enqueued ||
            state.Occurrence.PinnedRule.PublishedFlowVersionId != intent.PublishedFlowVersionId ||
            !HaveSamePinnedRule(state.Occurrence.PinnedRule, intent.PinnedRule) || state.Occurrence.RaisedEvidence != intent.PinnedEvidence ||
            intent.StableFlowDispatchKey != FlowDispatchKey(state.Series, state.Occurrence, intent.PublishedFlowVersionId) ||
            state.LatestEvidence?.Classification != MonitoringClassification.Breach || !IsCurrentFresh(state.LatestEvidence, currentRule, now)) return false;
        return !ActiveBypasses(state.Series, bypasses, now, groups).Any();
    }

    private static bool IsDisk(MonitoringRuleDto rule) =>
        rule.Condition.Kind is MonitoringMetricKind.DiskFreePercent or MonitoringMetricKind.DiskFreeSpace;

    private static bool IsWellFormedDiskCollection(MonitoringDiskCollectionStamp? collection) =>
        collection is { CollectionId: var id, CollectedAtUtc: var at, PayloadFingerprint: var fingerprint } &&
        id != Guid.Empty && at != default && fingerprint is { Length: 64 } &&
        fingerprint.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string? DiskCollectionReason(MonitoringSeriesState state, MonitoringObservationDto observation, DateTimeOffset now)
    {
        if (observation.ReceivedAtUtc == default) return "invalid_clock";
        if (observation.ReceivedAtUtc > now) return "future_clock";
        if (PreviousDiskTransportReceipt(state, observation, now) is { } previousReceipt && observation.ReceivedAtUtc <= previousReceipt)
            return "reordered_clock";
        if (!IsWellFormedDiskCollection(observation.DiskCollection)) return "disk_collection_metadata_required";
        var collection = observation.DiskCollection!;
        if (observation.ObservedAtUtc != collection.CollectedAtUtc) return "disk_collection_clock_mismatch";
        if (collection.CollectedAtUtc > now || observation.ReceivedAtUtc > now || collection.CollectedAtUtc > observation.ReceivedAtUtc)
            return "future_clock";
        if (state.LastDiskCollection is not { } previous) return null;
        if (previous.CollectionId == collection.CollectionId)
            return previous == collection ? null : "disk_collection_changed";
        // IDs are opaque, and the high-water marker survives missing/Unknown evidence.
        return collection.CollectedAtUtc <= previous.CollectedAtUtc ? "reordered_disk_collection" : null;
    }

    private static DateTimeOffset? PreviousDiskTransportReceipt(MonitoringSeriesState state, MonitoringObservationDto observation, DateTimeOffset now)
    {
        // Preserve the historical same-epoch receipt-clock check. A committed new
        // connection epoch establishes its own server receipt clock; a mere stream
        // retry, actor restart or operator boundary does not reset this high water.
        if (state.Cursor?.ConnectionEpoch != observation.Cursor.ConnectionEpoch) return null;
        if (state.LastDiskTransportReceivedAtUtc is { } admittedReceipt) return admittedReceipt;
        var previous = state.LatestEvidence?.ReceivedAtUtc;
        return previous is { } receipt && receipt != default && receipt <= now ? receipt : null;
    }

    private MonitoringEvaluationResult EvaluateCachedDiskCollection(MonitoringSeriesState state, MonitoringRuleDto rule,
        MonitoringObservationDto observation, IReadOnlyList<MonitoringBypassDto> bypasses, DateTimeOffset now,
        IReadOnlyList<MonitoringGroupDto>? groups)
    {
        var changedEpoch = state.Cursor is { } previousCursor && previousCursor.ConnectionEpoch != observation.Cursor.ConnectionEpoch;
        var changedStream = state.EvidenceStreamId != observation.EvidenceStreamId ||
            state.LatestEvidence?.EvidenceStreamId != observation.EvidenceStreamId;
        var working = state with { Cursor = observation.Cursor, EvidenceStreamId = observation.EvidenceStreamId,
            LastDiskTransportReceivedAtUtc = observation.ReceivedAtUtc };
        // A copied physical collection can consume a transport cursor, but cannot
        // establish continuity/freshness after a restart, missing sample or stream change.
        if (changedEpoch || changedStream || working.EvidenceQuality != MonitoringEvidenceQuality.Fresh ||
            working.LatestEvidence?.DiskCollection != working.LastDiskCollection)
            working = InterruptContinuity(working) with
            {
                EvidenceQuality = MonitoringEvidenceQuality.Unknown,
                LatestEvidence = working.LatestEvidence is { } previous ? previous with
                {
                    Quality = MonitoringEvidenceQuality.Unknown, Classification = MonitoringClassification.Unknown,
                    UnknownReason = changedEpoch || changedStream ? "stream_changed" :
                        previous.Quality == MonitoringEvidenceQuality.Unknown ? previous.UnknownReason ?? "disk_collection_unavailable" : "disk_collection_unavailable"
                } : null
            };
        working = WithSuppression(working, bypasses, now, groups);
        if (!IsCurrentFresh(working.LatestEvidence, rule, now))
            working = InterruptContinuity(working) with
            {
                EvidenceQuality = MonitoringEvidenceQuality.Unknown,
                LatestEvidence = working.LatestEvidence is { Quality: MonitoringEvidenceQuality.Fresh } previous ? previous with
                {
                    Quality = MonitoringEvidenceQuality.Unknown, Classification = MonitoringClassification.Unknown, UnknownReason = "stale"
                } : working.LatestEvidence
            };
        var outbox = ImmutableArray.CreateBuilder<MonitoringOutboxIntent>();
        // This is the same permitted release of an already raised suppressed event
        // as Refresh; it never raises/resolves an occurrence or completes a hold.
        working = AddDispatchIfEligible(working, now, rule, outbox);
        return Result(state, working with { StateRevision = checked(state.StateRevision + 1) }, [], outbox.ToImmutable());
    }

    private static bool HaveSamePinnedRule(MonitoringRuleDto left, MonitoringRuleDto right) =>
        left with { Targets = right.Targets, Condition = right.Condition } == right &&
        left.Targets.Mode == right.Targets.Mode && SameArray(left.Targets.AgentIds, right.Targets.AgentIds) &&
        SameArray(left.Targets.GroupIds, right.Targets.GroupIds) &&
        left.Condition with { ExpectedServiceStates = right.Condition.ExpectedServiceStates } == right.Condition &&
        SameArray(left.Condition.ExpectedServiceStates, right.Condition.ExpectedServiceStates);

    private static bool SameArray<T>(ImmutableArray<T> left, ImmutableArray<T> right) =>
        left.IsDefault == right.IsDefault && (left.IsDefault || left.SequenceEqual(right));

    /// <summary>Validates receipt data before any identical-outcome fast path.</summary>
    public static void RequireFlowOutcome(MonitoringFlowOutcomeDto outcome, DateTimeOffset now)
    {
        if (outcome.FlowRunId == Guid.Empty ||
            (outcome.FlowRunId is null && (outcome.Outcome is MonitoringFlowOutcomeKind.Succeeded or MonitoringFlowOutcomeKind.Skipped || outcome.Code is null)) ||
            !Enum.IsDefined(outcome.Outcome) || outcome.OccurredAtUtc == default ||
            outcome.OccurredAtUtc > now || (outcome.Code is { } code &&
                (code.Length == 0 || code.Length > 64 || code.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))))
            throw new ArgumentException("invalid_flow_outcome");
        if (outcome.Receipt is { } receipt && (outcome.Outcome != MonitoringFlowOutcomeKind.Succeeded ||
            !MonitoringContractValidator.TryValidateIncidentReceipt(receipt, out _))) throw new ArgumentException("invalid_incident_receipt");
    }

    public MonitoringEvaluationResult RecordFlowOutcome(MonitoringSeriesState state, Guid occurrenceId, Guid eventId, MonitoringFlowOutcomeDto outcome)
    {
        RequireFlowOutcome(outcome, timeProvider.GetUtcNow());
        if (state.Occurrence?.OccurrenceId != occurrenceId || state.Occurrence.RaisedEventId != eventId || state.Occurrence.PinnedRule.PublishedFlowVersionId is null)
            throw new ArgumentException("invalid_flow_occurrence");
        if (state.Occurrence.FlowOutcome is null && state.Occurrence.FlowDispatchDisposition != MonitoringFlowDispatchDisposition.Enqueued)
            throw new InvalidOperationException("enqueued_flow_required");
        if (state.Occurrence.FlowOutcome == outcome) return Unchanged(state);
        if (state.Occurrence.FlowOutcome is { } previous && ((previous.FlowRunId != outcome.FlowRunId && !(previous.FlowRunId is null && outcome.FlowRunId is not null)) || previous.Outcome != MonitoringFlowOutcomeKind.DeliveryUnknown ||
            outcome.OccurredAtUtc < previous.OccurredAtUtc)) throw new InvalidOperationException("flow_outcome_is_final");
        var disposition = outcome.Outcome switch
        {
            MonitoringFlowOutcomeKind.Succeeded or MonitoringFlowOutcomeKind.Skipped => MonitoringFlowDispatchDisposition.Completed,
            MonitoringFlowOutcomeKind.Failed => MonitoringFlowDispatchDisposition.Failed,
            _ => MonitoringFlowDispatchDisposition.DeliveryUnknown
        };
        var working = state with { StateRevision = checked(state.StateRevision + 1), Occurrence = state.Occurrence with { FlowDispatchDisposition = disposition, FlowOutcome = outcome } };
        return Result(state, working, [], []);
    }

    private MonitoringEvidenceDto Classify(MonitoringSeriesState state, MonitoringRuleDto rule, MonitoringObservationDto observation, DateTimeOffset now, string? collectionReason = null)
    {
        var reason = collectionReason ?? (!observation.Complete ? "partial" : !observation.Supported ? "unsupported" :
            observation.ObservedAtUtc == default || observation.ReceivedAtUtc == default ? "invalid_clock" :
            observation.ObservedAtUtc > now || observation.ReceivedAtUtc > now || observation.ObservedAtUtc > observation.ReceivedAtUtc ? "future_clock" :
            now - observation.ObservedAtUtc > rule.FreshnessBudget || now - observation.ReceivedAtUtc > rule.FreshnessBudget ? "stale" :
            state.NotBeforeObservedAtUtc is { } observedFence && observation.ObservedAtUtc <= observedFence ? "before_evaluation_fence" :
            state.NotBeforeReceivedAtUtc is { } receivedFence && observation.ReceivedAtUtc <= receivedFence ? "before_receipt_fence" :
            state.LatestEvidence is { } previous && observation.Cursor.ConnectionEpoch == previous.Cursor.ConnectionEpoch &&
                !IsDisk(rule) && (observation.ObservedAtUtc <= previous.ObservedAtUtc || observation.ReceivedAtUtc <= previous.ReceivedAtUtc) ? "reordered_clock" : null);
        var classification = MonitoringClassification.Unknown;
        if (reason is null)
        {
            var condition = rule.Condition;
            if (condition.Kind == MonitoringMetricKind.ServiceExpectedState)
            {
                reason = observation.ServiceState is null or ClientServiceState.Unknown or ClientServiceState.Unsupported || !Enum.IsDefined(observation.ServiceState.Value) ? "unknown_service" :
                    (observation.ServiceState == ClientServiceState.Missing) != observation.AuthoritativeMissing ? "unproven_missing" :
                    observation.ServiceWatchPolicyRevision is null or 0 || observation.CurrentServiceWatchPolicyRevision is null or 0 ||
                    observation.ServiceWatchPolicyRevision != observation.CurrentServiceWatchPolicyRevision ? "stale_watch_policy" : null;
                if (reason is null) classification = condition.ExpectedServiceStates.Contains(observation.ServiceState!.Value) ? MonitoringClassification.Recovery : MonitoringClassification.Breach;
            }
            else if (observation.NumericValue is not double value || !double.IsFinite(value) || !double.IsFinite(observation.NumericResolution) || observation.NumericResolution < 0 ||
                value < 0 || value > (condition.Kind == MonitoringMetricKind.DiskFreeSpace ? Math.Pow(1024, 6) : 100) ||
                observation.NumericResolution > (condition.Kind == MonitoringMetricKind.DiskFreeSpace ? Math.Pow(1024, 6) : 100)) reason = "invalid_number";
            else
            {
                var breach = condition.Kind == MonitoringMetricKind.DiskFreeSpace ? MonitoringContractValidator.ToCanonicalBytes(condition.BreachThreshold!.Value, condition.Unit!.Value) : condition.BreachThreshold!.Value;
                var recovery = condition.Kind == MonitoringMetricKind.DiskFreeSpace ? MonitoringContractValidator.ToCanonicalBytes(condition.RecoveryThreshold!.Value, condition.Unit!.Value) : condition.RecoveryThreshold!.Value;
                var lower = Math.Max(0, value - observation.NumericResolution / 2);
                var upper = value + observation.NumericResolution / 2;
                if (observation.NumericResolution > 0 && ((lower <= breach && upper >= breach) || (lower <= recovery && upper >= recovery)))
                {
                    reason = "numeric_uncertainty";
                }
                else
                classification = condition.Kind == MonitoringMetricKind.CpuUsagePercent
                    ? lower > breach ? MonitoringClassification.Breach : upper < recovery ? MonitoringClassification.Recovery : MonitoringClassification.Neutral
                    : upper < breach ? MonitoringClassification.Breach : lower > recovery ? MonitoringClassification.Recovery : MonitoringClassification.Neutral;
            }
        }
        return new(observation.Cursor, observation.EvidenceStreamId, observation.ObservedAtUtc, observation.ReceivedAtUtc,
            reason is null ? MonitoringEvidenceQuality.Fresh : MonitoringEvidenceQuality.Unknown, classification,
            observation.NumericValue, observation.NumericResolution, observation.ServiceState, reason,
            IsWellFormedDiskCollection(observation.DiskCollection) ? observation.DiskCollection : null);
    }

    private MonitoringEvaluationResult Suspend(MonitoringSeriesState state, MonitoringRuleDto rule, MonitoringClosureDisposition disposition, Guid? operatorId, string? reason)
    {
        var phase = disposition == MonitoringClosureDisposition.TargetRemoved ? MonitoringPhase.NotApplicable : MonitoringPhase.Suspended;
        if (state.Phase == phase && !HasOpenOccurrence(state) && operatorId is null) return Unchanged(state,
            disposition == MonitoringClosureDisposition.TargetRemoved ? MonitoringEvaluationDisposition.TargetNotApplicable : MonitoringEvaluationDisposition.RuleDisabled);
        var now = timeProvider.GetUtcNow();
        var events = ImmutableArray<MonitoringEventIntent>.Empty;
        var occurrence = state.Occurrence;
        if (HasOpenOccurrence(state))
        {
            occurrence = occurrence! with { EndedAtUtc = now, ClosureDisposition = disposition };
            events = [new(_nextId(), MonitoringEventKind.AlertSuspended, state.Series, occurrence.OccurrenceId, now, occurrence.PinnedRule, state.LatestEvidence!, disposition, reason)];
        }
        var working = state with { StateRevision = checked(state.StateRevision + 1), Phase = phase, EvidenceQuality = MonitoringEvidenceQuality.Unknown,
            Occurrence = occurrence, WindowStartedAtUtc = null, WindowStartedObservedAtUtc = null, PreviousQualifyingReceivedAtUtc = null, NotBeforeObservedAtUtc = now, NotBeforeReceivedAtUtc = now };
        return Result(state, working, events, [], operatorId is { } actor ? [Audit(state, rule, actor, "suspend", reason!, now)] : []);
    }

    private MonitoringSeriesState AddDispatchIfEligible(MonitoringSeriesState state, DateTimeOffset now, MonitoringRuleDto rule, ImmutableArray<MonitoringOutboxIntent>.Builder outbox)
    {
        if (state.Suppressed || !rule.Enabled || !HasOpenOccurrence(state) || state.Occurrence!.FlowDispatchDisposition != MonitoringFlowDispatchDisposition.Suppressed ||
            state.LatestEvidence?.Classification != MonitoringClassification.Breach || !IsCurrentFresh(state.LatestEvidence, rule, now)) return state;
        var occurrence = state.Occurrence;
        var pinned = occurrence.PinnedRule;
        if (pinned.PublishedFlowVersionId is not Guid flow) return state;
        var key = FlowDispatchKey(state.Series, occurrence, flow);
        outbox.Add(new(key, occurrence.RaisedEventId, occurrence.OccurrenceId, state.Series, flow, now, pinned, occurrence.RaisedEvidence));
        return state with { Occurrence = occurrence with { FlowDispatchDisposition = MonitoringFlowDispatchDisposition.Enqueued } };
    }

    private static string FlowDispatchKey(MonitoringSeriesKey series, MonitoringOccurrenceDto occurrence, Guid flow) =>
        $"monitoring:v1:{series.TenantId}:{occurrence.OccurrenceId:D}:{occurrence.RaisedEventId:D}:{flow:D}";

    private static MonitoringSeriesState InterruptContinuity(MonitoringSeriesState state) => state with
    {
        Phase = HasOpenOccurrence(state) ? MonitoringPhase.Firing : state.Phase == MonitoringPhase.Pending ? MonitoringPhase.Healthy : state.Phase,
        WindowStartedAtUtc = null, WindowStartedObservedAtUtc = null, PreviousQualifyingReceivedAtUtc = null
    };

    private static bool IsNewCursor(MonitoringAcceptedCursor incoming, MonitoringAcceptedCursor? current) =>
        current is null || incoming.ConnectionEpoch > current.ConnectionEpoch || (incoming.ConnectionEpoch == current.ConnectionEpoch && incoming.Sequence > current.Sequence);

    private static bool IsCurrentFresh(MonitoringEvidenceDto? evidence, MonitoringRuleDto rule, DateTimeOffset now) =>
        evidence is { Quality: MonitoringEvidenceQuality.Fresh } && evidence.ObservedAtUtc <= now && evidence.ReceivedAtUtc <= now &&
        now - evidence.ObservedAtUtc <= rule.FreshnessBudget && now - evidence.ReceivedAtUtc <= rule.FreshnessBudget;

    private static bool HasOpenOccurrence(MonitoringSeriesState state) => state.Occurrence is { EndedAtUtc: null };

    private static MonitoringSeriesState WithSuppression(MonitoringSeriesState state, IReadOnlyList<MonitoringBypassDto> bypasses, DateTimeOffset now, IReadOnlyList<MonitoringGroupDto>? groups)
    {
        var active = ActiveBypasses(state.Series, bypasses, now, groups).Select(bypass => bypass.BypassId).Distinct().Order().ToImmutableArray();
        if (state.Suppressed == (active.Length > 0) && !state.ApplicableBypassIds.IsDefault && state.ApplicableBypassIds.SequenceEqual(active)) return state;
        return state with { Suppressed = active.Length > 0, ApplicableBypassIds = active };
    }

    private static IEnumerable<MonitoringBypassDto> ActiveBypasses(MonitoringSeriesKey series, IReadOnlyList<MonitoringBypassDto> bypasses, DateTimeOffset now, IReadOnlyList<MonitoringGroupDto>? groups)
    {
        if (bypasses.Count > MonitoringLimits.MaximumBypassesPerEvaluation) throw new ArgumentException("bypass_limit_exceeded");
        foreach (var bypass in bypasses)
        {
            if (!MonitoringContractValidator.TryValidateBypass(bypass, out _)) throw new ArgumentException("invalid_bypass");
            if (bypass.TenantId != series.TenantId || (bypass.RuleId is not null && bypass.RuleId != series.RuleId) ||
                (bypass.AgentId is not null && bypass.AgentId != series.AgentId) ||
                (bypass.ResourceKey is not null && bypass.ResourceKey != series.ResourceKey) ||
                bypass.StartsAtUtc > now || bypass.ExpiresAtUtc <= now) continue;
            var groupMatches = true;
            if (bypass.GroupId is { } groupId)
            {
                var matchingGroups = groups?.Where(group => group.GroupId == groupId && group.TenantId == series.TenantId).ToArray();
                if (matchingGroups is not { Length: 1 } || !MonitoringContractValidator.TryValidateGroup(matchingGroups[0], out _))
                    throw new ArgumentException("group_bypass_membership_required");
                groupMatches = matchingGroups[0].AgentIds.Contains(series.AgentId);
            }
            if (groupMatches && (bypass.RuleId is null || bypass.RuleId == series.RuleId) &&
                (bypass.AgentId is null || bypass.AgentId == series.AgentId) && (bypass.ResourceKey is null || bypass.ResourceKey == series.ResourceKey) &&
                bypass.StartsAtUtc <= now && (bypass.ExpiresAtUtc is null || bypass.ExpiresAtUtc > now)) yield return bypass;
        }
    }

    private static bool MatchesRule(MonitoringSeriesKey series, MonitoringRuleDto rule)
    {
        if (series.TenantId != rule.TenantId || series.RuleId != rule.RuleId || series.AgentId == Guid.Empty || string.IsNullOrWhiteSpace(series.ResourceKey) ||
            series.ResourceKey.Length > MonitoringLimits.MaximumResourceKeyLength || series.ResourceKey.Any(char.IsControl)) return false;
        return rule.Condition.Kind switch
        {
            MonitoringMetricKind.CpuUsagePercent => series.ResourceKey == "cpu",
            MonitoringMetricKind.DiskFreePercent or MonitoringMetricKind.DiskFreeSpace => series.ResourceKey.StartsWith("disk:", StringComparison.Ordinal) &&
                series.ResourceKey.Length > 5 && RemoteFilePath.TryNormalize(series.ResourceKey[5..], out var scope) && scope == series.ResourceKey[5..] &&
                (rule.Condition.ResourceName is null || series.ResourceKey == "disk:" + rule.Condition.ResourceName),
            MonitoringMetricKind.ServiceExpectedState => series.ResourceKey == ServiceResourceKey(rule.Condition.ResourceName!, rule.Condition.ServicePlatform!.Value),
            _ => false
        };
    }

    public static string ServiceResourceKey(string name, ClientServicePlatform platform)
    {
        if (platform is not (ClientServicePlatform.Windows or ClientServicePlatform.LinuxSystemd) || !ClientServiceContractValidator.IsValidServiceName(name, platform))
            throw new ArgumentException("invalid_service_resource");
        return platform == ClientServicePlatform.Windows ? "service:windows:" + name.ToUpperInvariant() : "service:systemd:" + name;
    }

    public static string DiskResourceKey(string scope) => RemoteFilePath.TryNormalize(scope, out var normalized)
        ? "disk:" + normalized : throw new ArgumentException("invalid_disk_scope");

    private static void RequireDefinition(MonitoringSeriesState state, MonitoringRuleDto rule)
    {
        MonitoringContractValidator.RequireRule(rule);
        if (state.Series.TenantId != rule.TenantId || state.Series.RuleId != rule.RuleId) throw new ArgumentException("foreign_rule");
        if (state.EvaluationRevision != rule.EvaluationRevision || state.EvaluationFingerprint != MonitoringContractValidator.EvaluationFingerprint(rule))
            throw new InvalidOperationException("explicit_condition_scope_reset_required");
    }

    private MonitoringAuditIntent Audit(MonitoringSeriesState state, MonitoringRuleDto rule, Guid actor, string operation, string reason, DateTimeOffset now) =>
        new(_nextId(), state.Series, operation, actor, reason, now, state.Occurrence?.OccurrenceId, rule.Revision);

    private static MonitoringEvaluationResult Result(MonitoringSeriesState previous, MonitoringSeriesState state,
        ImmutableArray<MonitoringEventIntent> events, ImmutableArray<MonitoringOutboxIntent> outbox, ImmutableArray<MonitoringAuditIntent> audits = default) =>
        new(MonitoringEvaluationDisposition.Accepted, previous.StateRevision, state, events, outbox, audits.IsDefault ? [] : audits);

    private static MonitoringEvaluationResult Unchanged(MonitoringSeriesState state, MonitoringEvaluationDisposition disposition = MonitoringEvaluationDisposition.Accepted) =>
        new(disposition, state.StateRevision, state, [], [], []);
}

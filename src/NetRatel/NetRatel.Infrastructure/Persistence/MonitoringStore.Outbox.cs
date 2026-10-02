using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Infrastructure.Persistence;

public sealed partial class MonitoringStore
{
    public async Task<ImmutableArray<MonitoringOutboxLease>> ClaimOutboxAsync(MonitoringOutboxClaimRequest request, CancellationToken cancellationToken)
    {
        RequireTenant(request.TenantId);
        if (request.WorkerId == Guid.Empty || request.MaximumCount is < 1 or > MonitoringLimits.MaximumOutboxClaims ||
            request.LeaseDuration <= TimeSpan.Zero || request.LeaseDuration > MonitoringLimits.MaximumOutboxLease) throw new ArgumentException("invalid_lease_claim");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var configRow = await LockConfigurationAsync(db, request.TenantId, cancellationToken, shared: true).ConfigureAwait(false);
        MonitoringConfigurationSnapshot? configuration = null;
        var now = NormalizeDatabaseTime(timeProvider.GetUtcNow());
        var pending = (short)MonitoringOutboxStatus.Pending; var leased = (short)MonitoringOutboxStatus.Leased;
        // Lock ordering is configuration -> evidence/series -> outbox. Read candidates without locks; each row is rechecked below.
        var candidates = await db.MonitoringFlowOutbox.AsNoTracking().Where(row => row.TenantId == request.TenantId &&
            (row.Status == MonitoringOutboxStatus.Pending && (row.NextAttemptAtUtc == null || row.NextAttemptAtUtc <= now) ||
             row.Status == MonitoringOutboxStatus.Leased && row.LeaseExpiresAtUtc <= now)).OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.OutboxId)
            .Take(request.MaximumCount * 2).ToListAsync(cancellationToken).ConfigureAwait(false);
        var claims = ImmutableArray.CreateBuilder<MonitoringOutboxLease>();
        foreach (var candidate in candidates)
        {
            if (claims.Count >= request.MaximumCount) break;
            var intent = Deserialize<MonitoringOutboxIntent>(candidate.IntentJson);
            var client = new ClientKey(intent.Series.TenantId, intent.Series.AgentId);
            MonitoringEvidenceFence? fence = null;
            var evidenceValid = false;
            var eligible = ImmutableArray<Guid>.Empty;
            if (candidate.FlowRunId is null)
            {
                configuration ??= await ReadConfigurationAsync(db, request.TenantId, configRow.Revision, cancellationToken).ConfigureAwait(false);
                if (await db.Agents.AsNoTracking().AnyAsync(agent => agent.TenantId == client.TenantId && agent.Id == client.AgentId &&
                    agent.IsEnabled && agent.Status == AgentStatus.Active && agent.RevokedAtUtc == null && agent.DeletedAtUtc == null && agent.SupersededAtUtc == null,
                    cancellationToken).ConfigureAwait(false)) eligible = [client.AgentId];
                fence = await directory.GetCurrentEvidenceAsync(client, cancellationToken).ConfigureAwait(false);
                evidenceValid = fence is not null && await LockAndCheckEvidenceAsync(db, fence, cancellationToken).ConfigureAwait(false);
            }
            var series = await LockSeriesAsync(db, intent.Series, cancellationToken).ConfigureAwait(false);
            var rows = await db.MonitoringFlowOutbox.FromSqlInterpolated($"""
                SELECT * FROM "MonitoringFlowOutbox" WHERE "OutboxId" = {candidate.OutboxId} AND "TenantId" = {request.TenantId}
                AND (("Status" = {pending} AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= {now}))
                    OR ("Status" = {leased} AND "LeaseExpiresAtUtc" <= {now})) FOR UPDATE SKIP LOCKED
                """).ToListAsync(cancellationToken).ConfigureAwait(false);
            var row = rows.SingleOrDefault();
            if (row is null) continue;
            if (row.FlowRunId is not null)
            {
                // A durable handoff is reconciled by its existing run, even after clear/offline/configuration changes.
                if (now - row.CreatedAtUtc > TimeSpan.FromMinutes(15))
                {
                    await FailUnstartedAsync(db, row, series is null ? null : ReadState(series), "handed_off_outcome_unknown", configRow.Revision, true, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                row.Status = MonitoringOutboxStatus.Leased; row.WorkerId = request.WorkerId; row.LeaseId = Guid.NewGuid();
                row.LeaseFence = checked(row.LeaseFence + 1); row.LeaseExpiresAtUtc = NormalizeDatabaseTime(now + request.LeaseDuration);
                claims.Add(new(row.OutboxId, intent, request.WorkerId, row.LeaseId.Value, row.LeaseFence, row.LeaseExpiresAtUtc.Value, row.Attempts, row.FlowRunId));
                continue;
            }
            var currentRule = configuration!.Rules.SingleOrDefault(rule => rule.RuleId == intent.Series.RuleId);
            var state = series is null ? null : ReadState(series);
            if (state is null || currentRule is null || !Applicable(currentRule, client.AgentId, configuration, eligible) ||
                state.Occurrence?.OccurrenceId != intent.OccurrenceId || state.Occurrence.EndedAtUtc is not null)
            {
                if (row.Attempts > 0)
                    await FailUnstartedAsync(db, row, state, "occurrence_unavailable_after_attempt", configRow.Revision, true, cancellationToken).ConfigureAwait(false);
                else { row.Status = MonitoringOutboxStatus.Cancelled; row.Code = "occurrence_unavailable"; }
                continue;
            }
            if (row.Attempts >= MonitoringLimits.MaximumOutboxAttempts || now - row.CreatedAtUtc > TimeSpan.FromMinutes(15))
            {
                var uncertain = row.Attempts > 0;
                await FailUnstartedAsync(db, row, state, uncertain ? "attempt_limit_unknown" : "pending_expired", configRow.Revision, uncertain, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!await publishedFlows.IsPublishedAsync(request.TenantId, intent.PublishedFlowVersionId, cancellationToken).ConfigureAwait(false))
            {
                await FailUnstartedAsync(db, row, state, "published_flow_unavailable", configRow.Revision, row.Attempts > 0, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!evidenceValid || fence is null || state.Cursor?.ConnectionEpoch != fence.ConnectionEpoch ||
                !_evaluator.CanDispatch(state, currentRule, intent, fence.EvidenceStreamId, configuration.Bypasses, configuration.Groups))
                continue;
            row.Status = MonitoringOutboxStatus.Leased; row.WorkerId = request.WorkerId; row.LeaseId = Guid.NewGuid();
            row.LeaseFence = checked(row.LeaseFence + 1); row.LeaseExpiresAtUtc = NormalizeDatabaseTime(now + request.LeaseDuration); row.Attempts = checked(row.Attempts + 1);
            claims.Add(new(row.OutboxId, intent, request.WorkerId, row.LeaseId.Value, row.LeaseFence, row.LeaseExpiresAtUtc.Value, row.Attempts));
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claims.ToImmutable();
    }

    private async Task FailUnstartedAsync(OrchestratorDbContext db, MonitoringFlowOutboxRecord row, MonitoringSeriesState? state,
        string code, ulong configurationRevision, bool uncertain, CancellationToken ct)
    {
        var outcome = new MonitoringFlowOutcomeDto(row.FlowRunId, uncertain ? MonitoringFlowOutcomeKind.DeliveryUnknown : MonitoringFlowOutcomeKind.Failed, timeProvider.GetUtcNow(), code);
        row.Status = uncertain ? MonitoringOutboxStatus.DeliveryUnknown : MonitoringOutboxStatus.Failed;
        row.Code = code; row.OutcomeJson = Serialize(outcome); row.WorkerId = null; row.LeaseId = null; row.LeaseExpiresAtUtc = null;
        if (state?.Occurrence?.OccurrenceId == row.OccurrenceId)
        {
            var result = _evaluator.RecordFlowOutcome(state, row.OccurrenceId, row.EventId, outcome);
            await ApplyEvaluationAsync(db, result, configurationRevision, ct).ConfigureAwait(false);
        }
        else await ApplyHistoricalFlowOutcomeAsync(db, Deserialize<MonitoringOutboxIntent>(row.IntentJson), outcome, ct).ConfigureAwait(false);
    }

    public async Task<bool> CompleteOutboxAsync(MonitoringOutboxCompletion completion, CancellationToken cancellationToken)
    {
        var lease = completion.Lease;
        RequireSeries(lease.Intent.Series);
        if (lease.OutboxId == Guid.Empty || lease.WorkerId == Guid.Empty || lease.LeaseId == Guid.Empty || lease.LeaseFence <= 0 ||
            completion.Status is MonitoringOutboxStatus.Leased or MonitoringOutboxStatus.Cancelled || !Enum.IsDefined(completion.Status) ||
            completion.Code is { } code && (code.Length is < 1 or > 64 || code.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))))
            throw new ArgumentException("invalid_lease_completion");
        if (completion.Status is MonitoringOutboxStatus.Completed or MonitoringOutboxStatus.Skipped or MonitoringOutboxStatus.Failed or MonitoringOutboxStatus.DeliveryUnknown)
        {
            var expected = completion.Status switch { MonitoringOutboxStatus.Completed => MonitoringFlowOutcomeKind.Succeeded, MonitoringOutboxStatus.Skipped => MonitoringFlowOutcomeKind.Skipped,
                MonitoringOutboxStatus.Failed => MonitoringFlowOutcomeKind.Failed, _ => MonitoringFlowOutcomeKind.DeliveryUnknown };
            if (completion.Outcome?.Outcome != expected) throw new ArgumentException("matching_flow_outcome_required");
        }
        else if (completion.Outcome is not null) throw new ArgumentException("retry_cannot_complete_outcome");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var configRow = await LockConfigurationAsync(db, lease.Intent.Series.TenantId, cancellationToken).ConfigureAwait(false);
        var series = await LockSeriesAsync(db, lease.Intent.Series, cancellationToken).ConfigureAwait(false);
        var row = (await db.MonitoringFlowOutbox.FromSqlInterpolated($"""SELECT * FROM "MonitoringFlowOutbox" WHERE "OutboxId" = {lease.OutboxId} AND "TenantId" = {lease.Intent.Series.TenantId} FOR UPDATE""")
            .ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        var now = NormalizeDatabaseTime(timeProvider.GetUtcNow());
        if (row is null || row.Status != MonitoringOutboxStatus.Leased || row.WorkerId != lease.WorkerId || row.LeaseId != lease.LeaseId || row.LeaseFence != lease.LeaseFence ||
            row.LeaseExpiresAtUtc <= now || row.LeaseExpiresAtUtc != lease.LeaseExpiresAtUtc || row.Attempts != lease.Attempt ||
            Serialize(Deserialize<MonitoringOutboxIntent>(row.IntentJson)) != Serialize(lease.Intent)) return false;
        if (completion.Outcome?.FlowRunId is { } completedRun)
        {
            if (row.FlowRunId is { } knownRun && completedRun != knownRun) return false;
            row.FlowRunId = completedRun; row.HandedOffAtUtc ??= now;
        }
        else if (row.FlowRunId is not null && completion.Outcome is not null) throw new ArgumentException("known_flow_run_id_required");
        row.Code = completion.Code; row.WorkerId = null; row.LeaseId = null; row.LeaseExpiresAtUtc = null;
        if (completion.Status == MonitoringOutboxStatus.Pending)
        {
            row.Status = row.FlowRunId is not null || row.Attempts < MonitoringLimits.MaximumOutboxAttempts ? MonitoringOutboxStatus.Pending : MonitoringOutboxStatus.Failed;
            row.NextAttemptAtUtc = row.Status == MonitoringOutboxStatus.Pending ? now + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, row.Attempts))) : null;
            if (row.Status == MonitoringOutboxStatus.Failed)
            {
                row.Code = "attempt_limit";
                await FailUnstartedAsync(db, row, series is null ? null : ReadState(series), "attempt_limit", configRow.Revision, true, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            row.Status = completion.Status; row.OutcomeJson = Serialize(completion.Outcome);
            if (series is not null && ReadState(series).Occurrence?.OccurrenceId == lease.Intent.OccurrenceId)
            {
                var result = _evaluator.RecordFlowOutcome(ReadState(series), lease.Intent.OccurrenceId, lease.Intent.EventId, completion.Outcome!);
                await ApplyEvaluationAsync(db, result, configRow.Revision, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // A started run may finish after clear and a newer episode. Record only its historical occurrence.
                await ApplyHistoricalFlowOutcomeAsync(db, lease.Intent, completion.Outcome!, cancellationToken).ConfigureAwait(false);
            }
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

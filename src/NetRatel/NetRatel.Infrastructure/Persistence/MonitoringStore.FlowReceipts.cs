using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;
using Npgsql;

namespace NetRatel.Infrastructure.Persistence;

public sealed partial class MonitoringStore
{
    public async Task<bool> MarkOutboxHandedOffAsync(MonitoringOutboxLease lease, Guid realRunId, CancellationToken cancellationToken)
    {
        RequireSeries(lease.Intent.Series);
        if (realRunId == Guid.Empty) throw new ArgumentException("real_flow_run_required");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockConfigurationAsync(db, lease.Intent.Series.TenantId, cancellationToken).ConfigureAwait(false);
        var row = await LockOutboxAsync(db, lease.OutboxId, lease.Intent.Series.TenantId, cancellationToken).ConfigureAwait(false);
        if (row is null || !MatchesActiveLease(row, lease, timeProvider.GetUtcNow()) || row.FlowRunId is { } known && known != realRunId) return false;
        if (row.FlowRunId == realRunId) return true;
        row.FlowRunId = realRunId; row.HandedOffAtUtc = NormalizeDatabaseTime(timeProvider.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Bounded durable polling schedule keeps running receipts from starving later runs. This never dispatches actions.</summary>
    public async Task<ImmutableArray<MonitoringUnsettledFlowReceipt>> ListUnsettledFlowRunsAsync(int tenantId, int maximumCount, CancellationToken cancellationToken)
    {
        RequireTenant(tenantId);
        if (maximumCount is < 1 or > MonitoringLimits.MaximumOutboxClaims) throw new ArgumentException("invalid_receipt_count");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockConfigurationAsync(db, tenantId, cancellationToken, shared: true).ConfigureAwait(false);
        var now = NormalizeDatabaseTime(timeProvider.GetUtcNow());
        var pending = (short)MonitoringOutboxStatus.Pending; var leased = (short)MonitoringOutboxStatus.Leased; var unknown = (short)MonitoringOutboxStatus.DeliveryUnknown;
        var rows = await db.MonitoringFlowOutbox.FromSqlInterpolated($"""
            SELECT * FROM "MonitoringFlowOutbox" WHERE "TenantId" = {tenantId}
            AND (("FlowRunId" IS NOT NULL AND "Status" IN ({pending}, {leased}, {unknown}))
                OR ("FlowRunId" IS NULL AND "Status" = {unknown} AND "Attempts" > 0)) AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= {now})
            ORDER BY COALESCE("NextAttemptAtUtc", "CreatedAtUtc"), "OutboxId" LIMIT {maximumCount} FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = BudgetPage(rows, row => new MonitoringUnsettledFlowReceipt(row.OutboxId, Deserialize<MonitoringOutboxIntent>(row.IntentJson), row.FlowRunId));
        foreach (var row in page) row.NextAttemptAtUtc = now + TimeSpan.FromSeconds(5);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page.Select(row => new MonitoringUnsettledFlowReceipt(row.OutboxId, Deserialize<MonitoringOutboxIntent>(row.IntentJson), row.FlowRunId)).ToImmutableArray();
    }

    /// <summary>Only a verified immutable flow-store outcome may call this internal port. Exact tenant/event/run identity is retained.</summary>
    public async Task<bool> ReconcileOutboxOutcomeAsync(MonitoringUnsettledFlowReceipt receipt, MonitoringFlowOutcomeDto outcome, CancellationToken cancellationToken)
    {
        RequireSeries(receipt.Intent.Series);
        if (receipt.FlowRunId == Guid.Empty || outcome.FlowRunId is not Guid realRunId || realRunId == Guid.Empty ||
            receipt.FlowRunId is { } expectedRun && expectedRun != realRunId) throw new ArgumentException("exact_flow_run_outcome_required");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var config = await LockConfigurationAsync(db, receipt.Intent.Series.TenantId, cancellationToken).ConfigureAwait(false);
        var series = await LockSeriesAsync(db, receipt.Intent.Series, cancellationToken).ConfigureAwait(false);
        var row = await LockOutboxAsync(db, receipt.OutboxId, receipt.Intent.Series.TenantId, cancellationToken).ConfigureAwait(false);
        if (row is null || !MatchesIntent(row, receipt.Intent) || row.FlowRunId is { } knownRun && knownRun != realRunId) return false;
        if (row.FlowRunId is null)
        {
            if (receipt.FlowRunId is not null || row.Attempts <= 0 || row.Status != MonitoringOutboxStatus.DeliveryUnknown) return false;
            row.FlowRunId = realRunId; row.HandedOffAtUtc = NormalizeDatabaseTime(timeProvider.GetUtcNow());
        }
        var prior = row.OutcomeJson is null ? null : Deserialize<MonitoringFlowOutcomeDto>(row.OutcomeJson);
        if (prior == outcome) return true;
        if (row.Status is not (MonitoringOutboxStatus.Pending or MonitoringOutboxStatus.Leased or MonitoringOutboxStatus.DeliveryUnknown) ||
            prior is not null && prior.Outcome != MonitoringFlowOutcomeKind.DeliveryUnknown) return false;
        if (series is not null && ReadState(series).Occurrence?.OccurrenceId == receipt.Intent.OccurrenceId)
        {
            var result = _evaluator.RecordFlowOutcome(ReadState(series), receipt.Intent.OccurrenceId, receipt.Intent.EventId, outcome);
            await ApplyEvaluationAsync(db, result, config.Revision, cancellationToken).ConfigureAwait(false);
        }
        else await ApplyHistoricalFlowOutcomeAsync(db, receipt.Intent, outcome, cancellationToken).ConfigureAwait(false);
        row.Status = outcome.Outcome switch
        {
            MonitoringFlowOutcomeKind.Succeeded => MonitoringOutboxStatus.Completed,
            MonitoringFlowOutcomeKind.Skipped => MonitoringOutboxStatus.Skipped,
            MonitoringFlowOutcomeKind.Failed => MonitoringOutboxStatus.Failed,
            _ => MonitoringOutboxStatus.DeliveryUnknown
        };
        row.OutcomeJson = Serialize(outcome); row.Code = outcome.Code; row.WorkerId = null; row.LeaseId = null; row.LeaseExpiresAtUtc = null;
        row.LeaseFence = checked(row.LeaseFence + 1); row.NextAttemptAtUtc = null;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private async Task ApplyHistoricalFlowOutcomeAsync(OrchestratorDbContext db, MonitoringOutboxIntent intent, MonitoringFlowOutcomeDto outcome, CancellationToken ct)
    {
        var occurrence = await db.MonitoringOccurrences.SingleOrDefaultAsync(item => item.OccurrenceId == intent.OccurrenceId && item.TenantId == intent.Series.TenantId, ct).ConfigureAwait(false);
        if (occurrence is null || occurrence.RaisedEventId != intent.EventId || occurrence.RuleId != intent.Series.RuleId || occurrence.AgentId != intent.Series.AgentId || occurrence.ResourceKey != intent.Series.ResourceKey)
            throw new InvalidOperationException("flow_occurrence_unavailable");
        var dto = Deserialize<MonitoringOccurrenceDto>(occurrence.OccurrenceJson);
        var synthetic = new MonitoringSeriesState(intent.Series, 0, dto.PinnedRule.EvaluationRevision, MonitoringPhase.Resolved, MonitoringEvidenceQuality.Unknown, Occurrence: dto);
        var result = _evaluator.RecordFlowOutcome(synthetic, dto.OccurrenceId, dto.RaisedEventId, outcome);
        occurrence.OccurrenceJson = Serialize(result.State.Occurrence);
    }
    private static async Task<MonitoringFlowOutboxRecord?> LockOutboxAsync(OrchestratorDbContext db, Guid outbox, int tenant, CancellationToken ct) =>
        (await db.MonitoringFlowOutbox.FromSqlInterpolated($"""SELECT * FROM "MonitoringFlowOutbox" WHERE "OutboxId" = {outbox} AND "TenantId" = {tenant} FOR UPDATE""").ToListAsync(ct).ConfigureAwait(false)).SingleOrDefault();
    private static bool MatchesIntent(MonitoringFlowOutboxRecord row, MonitoringOutboxIntent intent) => row.EventId == intent.EventId && row.OccurrenceId == intent.OccurrenceId &&
        row.StableFlowDispatchKey == intent.StableFlowDispatchKey && Serialize(Deserialize<MonitoringOutboxIntent>(row.IntentJson)) == Serialize(intent);
    private static bool MatchesActiveLease(MonitoringFlowOutboxRecord row, MonitoringOutboxLease lease, DateTimeOffset now) =>
        row.Status == MonitoringOutboxStatus.Leased && row.WorkerId == lease.WorkerId && row.LeaseId == lease.LeaseId && row.LeaseFence == lease.LeaseFence &&
        row.LeaseExpiresAtUtc > now && row.LeaseExpiresAtUtc == lease.LeaseExpiresAtUtc && row.Attempts == lease.Attempt && MatchesIntent(row, lease.Intent);
    private static DateTimeOffset NormalizeDatabaseTime(DateTimeOffset time) => new(time.UtcTicks - time.UtcTicks % 10, TimeSpan.Zero);
}

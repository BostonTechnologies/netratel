using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Monitoring;
using Npgsql;

namespace NetRatel.Infrastructure.Persistence;

public sealed partial class MonitoringStore
{
    public async Task<MonitoringStoreWriteResult> CommitAsync(MonitoringCommitRequest request, CancellationToken cancellationToken)
    {
        var result = request.Evaluation;
        RequireSeries(result.State.Series);
        if (result.State.StateRevision < result.ExpectedStateRevision || result.State.StateRevision > checked(result.ExpectedStateRevision + 1))
            throw new ArgumentException("invalid_state_revision");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var configRow = await LockConfigurationAsync(db, result.State.Series.TenantId, cancellationToken).ConfigureAwait(false);
        if (configRow.Revision != request.ExpectedConfigurationRevision) return new(MonitoringStoreWriteDisposition.Conflict, null);
        if (request.ExpectedEvidenceFence is { } fence &&
            (!await LockAndCheckEvidenceAsync(db, fence, cancellationToken).ConfigureAwait(false) ||
                result.State.EvidenceStreamId != fence.EvidenceStreamId))
            return new(MonitoringStoreWriteDisposition.StaleEvidence, null);
        var current = await LockSeriesAsync(db, result.State.Series, cancellationToken).ConfigureAwait(false);
        if (request.ExpectedEvidenceFence is { } acceptedFence && result.State.Cursor != (current is null ? null : ReadState(current).Cursor) &&
            result.State.Cursor?.ConnectionEpoch != acceptedFence.ConnectionEpoch)
            return new(MonitoringStoreWriteDisposition.StaleEvidence, null);
        if (current is not null && result.State.Cursor is { } nextCursor && ReadState(current).Cursor is { } previousCursor && nextCursor != previousCursor &&
            (nextCursor.ConnectionEpoch < previousCursor.ConnectionEpoch || nextCursor.ConnectionEpoch == previousCursor.ConnectionEpoch && nextCursor.Sequence <= previousCursor.Sequence))
            return new(MonitoringStoreWriteDisposition.StaleEvidence, ReadState(current));
        if ((current?.StateRevision ?? 0) != result.ExpectedStateRevision)
            return new(MonitoringStoreWriteDisposition.Conflict, current is null ? null : ReadState(current));
        if (current is null && (await db.MonitoringSeries.CountAsync(row => row.TenantId == result.State.Series.TenantId && row.AgentId == result.State.Series.AgentId, cancellationToken) >= MonitoringLimits.MaximumSeriesPerClient ||
            await db.MonitoringSeries.CountAsync(row => row.TenantId == result.State.Series.TenantId, cancellationToken) >= MaximumSeriesPerTenant))
            throw new InvalidOperationException("series_capacity_exceeded");
        if (result.State.StateRevision == result.ExpectedStateRevision)
        {
            if (!result.Events.IsEmpty || !result.Outbox.IsEmpty || !result.Audits.IsEmpty || current is null || Serialize(result.State) != Serialize(ReadState(current)))
                throw new ArgumentException("invalid_unchanged_state");
            return new(MonitoringStoreWriteDisposition.Stored, result.State);
        }
        if (result.State.Occurrence is { } occurrence)
        {
            var identity = current is null ? null : ReadState(current).Occurrence?.ClientIdentity;
            if (identity is null)
            {
                var agent = await db.Agents.IgnoreQueryFilters().AsNoTracking().Where(agent => agent.TenantId == result.State.Series.TenantId && agent.Id == result.State.Series.AgentId)
                    .Select(agent => new { agent.Name, agent.DeviceInfoJson, agent.DeletedAtUtc }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                identity = MonitoringIdentityPresentation.Create(result.State.Series.AgentId, agent?.Name, agent?.DeviceInfoJson, agent?.DeletedAtUtc is not null);
            }
            result = result with
            {
                State = result.State with { Occurrence = occurrence with { ClientIdentity = identity } },
                Events = result.Events.Select(item => item with { ClientIdentity = identity }).ToImmutableArray()
            };
        }
        try
        {
            if (request.ExpectedEvidenceFence is { } mutationFence && !await CheckEpochAsync(db, mutationFence, cancellationToken).ConfigureAwait(false))
                return new(MonitoringStoreWriteDisposition.StaleEvidence, null);
            await ApplyEvaluationAsync(db, result, request.ExpectedConfigurationRevision, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (request.ExpectedEvidenceFence is { } commitFence && !await CheckEpochAsync(db, commitFence, cancellationToken).ConfigureAwait(false))
                return new(MonitoringStoreWriteDisposition.StaleEvidence, null);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(MonitoringStoreWriteDisposition.Stored, result.State);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            // Do not query through a failed transaction or retain its row locks during the reload.
            await transaction.DisposeAsync().ConfigureAwait(false);
            return new(MonitoringStoreWriteDisposition.Conflict, await LoadSeriesAsync(result.State.Series, cancellationToken).ConfigureAwait(false));
        }
    }

    public async Task<bool> BeginEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken cancellationToken)
    {
        RequireFence(fence);
        if (fence.RegistrationOrdinal <= 0) return false;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await CheckEpochAsync(db, fence, cancellationToken).ConfigureAwait(false)) return false;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MonitoringEvidenceStreams" ("TenantId","AgentId","ConnectionId","ConnectionEpoch","EvidenceStreamId","Active","RegisteredAtUtc","Revision","CommittedRegistrationOrdinal")
            VALUES ({fence.Client.TenantId},{fence.Client.AgentId},{fence.ConnectionId},{fence.ConnectionEpoch},{fence.EvidenceStreamId},FALSE,{timeProvider.GetUtcNow()},0,0)
            ON CONFLICT ("TenantId","AgentId") DO NOTHING
            """, cancellationToken).ConfigureAwait(false);
        var row = await LockEvidenceAsync(db, fence.Client, cancellationToken).ConfigureAwait(false);
        // Local physical presentation is required only at ingress. A pending retry leaves the current committed stream untouched.
        if (row is null || !await directory.IsPresentedEvidenceAsync(fence, cancellationToken).ConfigureAwait(false)) return false;
        if (row.Active && MatchesFence(row, fence)) return true;
        if (row.CommittedRegistrationOrdinal >= fence.RegistrationOrdinal) return false;
        var attempt = (await db.Set<MonitoringEvidenceRegistrationAttemptRecord>().FromSqlInterpolated($"""
            SELECT * FROM "MonitoringEvidenceRegistrationAttempts" WHERE "TenantId"={fence.Client.TenantId} AND "AgentId"={fence.Client.AgentId}
            AND "RegistrationId"={fence.EvidenceStreamId} FOR UPDATE
            """).ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        var now = await EvidenceNowAsync(db, timeProvider, cancellationToken).ConfigureAwait(false);
        if (attempt is not { Status: 1 } || attempt.ConnectionId != fence.ConnectionId || attempt.ConnectionEpoch != fence.ConnectionEpoch ||
            attempt.RegistrationOrdinal != fence.RegistrationOrdinal || attempt.ExpiresAtUtc <= now) return false;
        // The owner lock is already held. Refresh its real deadlines after all awaited evidence/attempt locks.
        if (!await CheckEpochAsync(db, fence, cancellationToken).ConfigureAwait(false) ||
            !await directory.IsPresentedEvidenceAsync(fence, cancellationToken).ConfigureAwait(false)) return false;
        row.ConnectionId = fence.ConnectionId; row.ConnectionEpoch = fence.ConnectionEpoch;
        row.EvidenceStreamId = fence.EvidenceStreamId; row.CommittedRegistrationOrdinal = fence.RegistrationOrdinal;
        row.Active = true; row.RegisteredAtUtc = now; row.Revision = checked(row.Revision + 1);
        attempt.Status = 2;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (!await CheckEpochAsync(db, fence, cancellationToken).ConfigureAwait(false) ||
            attempt.ExpiresAtUtc <= await EvidenceNowAsync(db, timeProvider, cancellationToken).ConfigureAwait(false)) return false;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> EndEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken cancellationToken)
    {
        RequireFence(fence);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // End selects only the exact registration. It cannot retire a successor or the Presence owner.
        var row = await LockEvidenceAsync(db, fence.Client, cancellationToken).ConfigureAwait(false);
        var matched = row is not null && MatchesFence(row, fence);
        if (matched && row!.Active) { row.Active = false; row.Revision = checked(row.Revision + 1); }
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MonitoringEvidenceRegistrationAttempts" SET "Status"=3
            WHERE "TenantId"={fence.Client.TenantId} AND "AgentId"={fence.Client.AgentId} AND "RegistrationId"={fence.EvidenceStreamId}
            AND "ConnectionId"={fence.ConnectionId} AND "ConnectionEpoch"={fence.ConnectionEpoch} AND "RegistrationOrdinal"={fence.RegistrationOrdinal}
            """, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return matched;
    }

    private static async Task<MonitoringTenantConfigurationRecord> LockConfigurationAsync(OrchestratorDbContext db, int tenant, CancellationToken ct, bool shared = false)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "MonitoringTenantConfigurations" ("TenantId", "Revision") VALUES ({tenant}, 0) ON CONFLICT ("TenantId") DO NOTHING""", ct).ConfigureAwait(false);
        var sql = shared ? $"SELECT * FROM \"MonitoringTenantConfigurations\" WHERE \"TenantId\" = {{0}} FOR SHARE" :
            $"SELECT * FROM \"MonitoringTenantConfigurations\" WHERE \"TenantId\" = {{0}} FOR UPDATE";
        var rows = await db.MonitoringTenantConfigurations.FromSqlRaw(sql, tenant).ToListAsync(ct).ConfigureAwait(false);
        return rows.Single();
    }

    private static async Task<MonitoringSeriesRecord?> LockSeriesAsync(OrchestratorDbContext db, MonitoringSeriesKey key, CancellationToken ct) =>
        (await db.MonitoringSeries.FromSqlInterpolated($"""SELECT * FROM "MonitoringSeries" WHERE "TenantId" = {key.TenantId} AND "RuleId" = {key.RuleId} AND "AgentId" = {key.AgentId} AND "ResourceKey" = {key.ResourceKey} FOR UPDATE""")
            .ToListAsync(ct).ConfigureAwait(false)).SingleOrDefault();
    private static async Task<MonitoringEvidenceStreamRecord?> LockEvidenceAsync(OrchestratorDbContext db, ClientKey key, CancellationToken ct, bool shared = false)
    {
        var sql = shared ? "SELECT * FROM \"MonitoringEvidenceStreams\" WHERE \"TenantId\" = {0} AND \"AgentId\" = {1} FOR SHARE" :
            "SELECT * FROM \"MonitoringEvidenceStreams\" WHERE \"TenantId\" = {0} AND \"AgentId\" = {1} FOR UPDATE";
        return (await db.MonitoringEvidenceStreams.FromSqlRaw(sql, key.TenantId, key.AgentId).ToListAsync(ct).ConfigureAwait(false)).SingleOrDefault();
    }
    // LastIssuedEpoch remains reservation-only. The accepted writer holds the
    // exact committed owner FOR SHARE until its existing transaction completes.
    private Task<bool> CheckEpochAsync(OrchestratorDbContext db, MonitoringEvidenceFence fence, CancellationToken ct) =>
        ClientConnectionEpochStore.LockEffectiveOwnerAsync(db,
            new OwnerKey(fence.Client, fence.ConnectionId, fence.ConnectionEpoch), timeProvider, ct);

    private async Task<bool> LockAndCheckEvidenceAsync(OrchestratorDbContext db, MonitoringEvidenceFence fence, CancellationToken ct)
    {
        RequireFence(fence);
        if (!await CheckEpochAsync(db, fence, ct).ConfigureAwait(false)) return false;
        var row = await LockEvidenceAsync(db, fence.Client, ct, shared: true).ConfigureAwait(false);
        // The physical gateway presents its own fence and retains its local
        // registration check. Shared workers validate this durable exact fence;
        // their empty or stale process-local registry cannot select authority.
        return row is { Active: true } && MatchesFence(row, fence);
    }
    // Read a candidate without locks, then validate owner -> evidence under the
    // caller transaction. A replacement between reads yields no candidate; the
    // worker may try later, without manufacturing a physical ingress identity.
    private async Task<MonitoringEvidenceFence?> LockCurrentEvidenceAsync(OrchestratorDbContext db, ClientKey client, CancellationToken ct)
    {
        var candidate = await db.MonitoringEvidenceStreams.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TenantId == client.TenantId && row.AgentId == client.AgentId, ct).ConfigureAwait(false);
        if (candidate is not { Active: true }) return null;
        var fence = new MonitoringEvidenceFence(client, candidate.ConnectionId, candidate.ConnectionEpoch, candidate.EvidenceStreamId, candidate.CommittedRegistrationOrdinal);
        return await LockAndCheckEvidenceAsync(db, fence, ct).ConfigureAwait(false) ? fence : null;
    }

    private static bool MatchesFence(MonitoringEvidenceStreamRecord row, MonitoringEvidenceFence fence) => row.ConnectionId == fence.ConnectionId &&
        row.ConnectionEpoch == fence.ConnectionEpoch && row.EvidenceStreamId == fence.EvidenceStreamId &&
        row.CommittedRegistrationOrdinal > 0 && row.CommittedRegistrationOrdinal == fence.RegistrationOrdinal;
    private static void RequireFence(MonitoringEvidenceFence fence)
    {
        if (!fence.Client.IsValid || fence.ConnectionId == Guid.Empty || fence.EvidenceStreamId == Guid.Empty || fence.ConnectionEpoch <= 0)
            throw new ArgumentException("invalid_evidence_fence");
    }

    private async Task ApplyEvaluationAsync(OrchestratorDbContext db, MonitoringEvaluationResult result, ulong configRevision, CancellationToken ct)
    {
        var state = result.State;
        var now = timeProvider.GetUtcNow();
        await EnforceHistoryCapacityAsync(db, state.Series.TenantId, result.Events.Length, result.Audits.Length, result.Outbox.Length, ct).ConfigureAwait(false);
        var row = db.MonitoringSeries.Local.SingleOrDefault(item => item.TenantId == state.Series.TenantId && item.RuleId == state.Series.RuleId && item.AgentId == state.Series.AgentId && item.ResourceKey == state.Series.ResourceKey)
            ?? await SeriesQuery(db, state.Series).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new() { TenantId = state.Series.TenantId, RuleId = state.Series.RuleId, AgentId = state.Series.AgentId, ResourceKey = state.Series.ResourceKey };
            db.MonitoringSeries.Add(row);
        }
        if (state.Occurrence is { } occurrence)
        {
            var history = await db.MonitoringOccurrences.SingleOrDefaultAsync(item => item.OccurrenceId == occurrence.OccurrenceId, ct).ConfigureAwait(false);
            if (history is null)
            {
                if (!result.Events.Any(item => item.EventId == occurrence.RaisedEventId && item.Kind == MonitoringEventKind.AlertRaised && item.OccurrenceId == occurrence.OccurrenceId))
                    throw new ArgumentException("raised_event_required_for_new_occurrence");
                history = new() { OccurrenceId = occurrence.OccurrenceId, TenantId = state.Series.TenantId, RuleId = state.Series.RuleId,
                    AgentId = state.Series.AgentId, ResourceKey = state.Series.ResourceKey, RaisedEventId = occurrence.RaisedEventId, RaisedAtUtc = occurrence.RaisedAtUtc };
                db.MonitoringOccurrences.Add(history);
            }
            else if (history.TenantId != state.Series.TenantId || history.RuleId != state.Series.RuleId || history.AgentId != state.Series.AgentId ||
                history.ResourceKey != state.Series.ResourceKey || history.RaisedEventId != occurrence.RaisedEventId ||
                !PinnedOccurrenceMatches(Deserialize<MonitoringOccurrenceDto>(history.OccurrenceJson), occurrence))
                throw new ArgumentException("immutable_occurrence_conflict");
            history.EndedAtUtc = occurrence.EndedAtUtc; history.OccurrenceJson = Serialize(occurrence);
        }
        var stateJson = Serialize(state);
        var clientBytes = await db.Database.SqlQuery<long>($"""
            SELECT COALESCE(SUM(OCTET_LENGTH("StateJson"::text)), 0)::bigint AS "Value" FROM "MonitoringSeries"
            WHERE "TenantId" = {state.Series.TenantId} AND "AgentId" = {state.Series.AgentId}
            """).SingleAsync(ct).ConfigureAwait(false);
        var nextBytes = await db.Database.SqlQuery<long>($"""
            SELECT OCTET_LENGTH(({stateJson}::jsonb)::text)::bigint AS "Value"
            """).SingleAsync(ct).ConfigureAwait(false);
        var previousBytes = row.StateRevision == 0 ? 0 : System.Text.Encoding.UTF8.GetByteCount(row.StateJson);
        if (clientBytes - previousBytes + nextBytes > MaximumAggregateBytes - 1024) throw new InvalidOperationException("monitoring_client_state_size_exceeded");
        ApplyState(row, state, now);
        foreach (var intent in result.Events)
        {
            if (intent.Series != state.Series || intent.OccurrenceId != state.Occurrence?.OccurrenceId || intent.EventId == Guid.Empty)
                throw new ArgumentException("invalid_event_identity");
            db.MonitoringEvents.Add(new() { EventId = intent.EventId, TenantId = state.Series.TenantId, OccurrenceId = intent.OccurrenceId, AtUtc = intent.AtUtc, EventJson = Serialize(intent) });
            db.OutboxMessages.Add(new() { Id = intent.EventId, OccurredUtc = intent.AtUtc, Type = MonitoringLimits.NotificationEventPrefix + intent.Kind,
                PayloadJson = Serialize(intent), Source = "monitoring", CorrelationId = intent.OccurrenceId.ToString("N"), TenantId = intent.Series.TenantId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EntityId = intent.Series.AgentId.ToString("N"), Severity = intent.PinnedRule.Severity.ToString(), Message = intent.PinnedRule.Name, Status = OutboxStatuses.Published });
        }
        foreach (var intent in result.Outbox)
        {
            if (intent.Series != state.Series || intent.OccurrenceId != state.Occurrence?.OccurrenceId || intent.EventId != state.Occurrence.RaisedEventId ||
                intent.PublishedFlowVersionId != state.Occurrence.PinnedRule.PublishedFlowVersionId || intent.StableFlowDispatchKey.Length > 256)
                throw new ArgumentException("invalid_outbox_identity");
            db.MonitoringFlowOutbox.Add(new() { OutboxId = Guid.NewGuid(), TenantId = state.Series.TenantId, OccurrenceId = intent.OccurrenceId,
                EventId = intent.EventId, StableFlowDispatchKey = intent.StableFlowDispatchKey, IntentJson = Serialize(intent), CreatedAtUtc = now, Status = MonitoringOutboxStatus.Pending });
        }
        foreach (var audit in result.Audits)
        {
            MonitoringContractValidator.RequireReason(audit.OperatorId, audit.Reason);
            if (audit.Series != state.Series) throw new ArgumentException("invalid_audit_identity");
            db.MonitoringAudits.Add(new() { AuditId = audit.AuditId, TenantId = state.Series.TenantId, EntityKind = "series", EntityId = state.Series.RuleId,
                Operation = audit.Operation, OperatorId = audit.OperatorId, Reason = audit.Reason, AtUtc = audit.AtUtc, ConfigurationRevision = configRevision, DetailsJson = Serialize(audit) });
        }
        if (state.Occurrence is { EndedAtUtc: not null } ended)
        {
            // Leased means started/possibly started and remains fenced for receipt/outcome reconciliation.
            await db.MonitoringFlowOutbox.Where(item => item.TenantId == state.Series.TenantId && item.OccurrenceId == ended.OccurrenceId && item.Status == MonitoringOutboxStatus.Pending && item.FlowRunId == null && item.Attempts == 0)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MonitoringOutboxStatus.Cancelled), ct).ConfigureAwait(false);
        }
    }
    private static bool PinnedOccurrenceMatches(MonitoringOccurrenceDto old, MonitoringOccurrenceDto candidate) =>
        old.RaisedAtUtc == candidate.RaisedAtUtc && old.BreachSinceAtUtc == candidate.BreachSinceAtUtc &&
        Serialize(old.PinnedRule) == Serialize(candidate.PinnedRule) && old.RaisedEvidence == candidate.RaisedEvidence &&
        (old.EndedAtUtc is null || old.EndedAtUtc == candidate.EndedAtUtc && old.ClosureDisposition == candidate.ClosureDisposition);

    private async Task EnforceHistoryCapacityAsync(OrchestratorDbContext db, int tenant, int events, int audits, int outbox, CancellationToken ct)
    {
        if (events == 0 && audits == 0 && outbox == 0) return;
        var cutoff = timeProvider.GetUtcNow() - MonitoringLimits.HistoryRetention;
        // Remove only closed, terminal history. Open occurrence evidence and pending/leased actions remain authoritative.
        var expired = await db.MonitoringOccurrences.Where(row => row.TenantId == tenant && row.EndedAtUtc < cutoff &&
            !db.MonitoringSeries.Any(series => series.LatestOccurrenceId == row.OccurrenceId) &&
            !db.MonitoringFlowOutbox.Any(box => box.OccurrenceId == row.OccurrenceId && (box.Status == MonitoringOutboxStatus.Pending || box.Status == MonitoringOutboxStatus.Leased || box.Status == MonitoringOutboxStatus.DeliveryUnknown)))
            .OrderBy(row => row.EndedAtUtc).Select(row => row.OccurrenceId).Take(100).ToArrayAsync(ct).ConfigureAwait(false);
        if (expired.Length != 0)
        {
            var eventIds = await db.MonitoringEvents.Where(row => expired.Contains(row.OccurrenceId)).Select(row => row.EventId).ToArrayAsync(ct).ConfigureAwait(false);
            await db.MonitoringFlowOutbox.Where(row => expired.Contains(row.OccurrenceId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.OutboxMessages.Where(row => eventIds.Contains(row.Id) && row.Type.StartsWith(MonitoringLimits.NotificationEventPrefix)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.MonitoringEvents.Where(row => expired.Contains(row.OccurrenceId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.MonitoringOccurrences.Where(row => expired.Contains(row.OccurrenceId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        var oldAudits = await db.MonitoringAudits.Where(row => row.TenantId == tenant && row.AtUtc < cutoff).OrderBy(row => row.AtUtc).Select(row => row.AuditId).Take(100).ToArrayAsync(ct).ConfigureAwait(false);
        if (oldAudits.Length != 0) await db.MonitoringAudits.Where(row => oldAudits.Contains(row.AuditId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (await db.MonitoringEvents.CountAsync(row => row.TenantId == tenant, ct).ConfigureAwait(false) + events + db.ChangeTracker.Entries<MonitoringEventRecord>().Count(entry => entry.State == EntityState.Added) > MonitoringLimits.MaximumRetainedEventsPerTenant ||
            await db.MonitoringAudits.CountAsync(row => row.TenantId == tenant, ct).ConfigureAwait(false) + audits + db.ChangeTracker.Entries<MonitoringAuditRecord>().Count(entry => entry.State == EntityState.Added) > MonitoringLimits.MaximumRetainedAuditsPerTenant ||
            await db.MonitoringFlowOutbox.CountAsync(row => row.TenantId == tenant && (row.Status == MonitoringOutboxStatus.Pending || row.Status == MonitoringOutboxStatus.Leased || row.Status == MonitoringOutboxStatus.DeliveryUnknown), ct).ConfigureAwait(false) + outbox + db.ChangeTracker.Entries<MonitoringFlowOutboxRecord>().Count(entry => entry.State == EntityState.Added) > MonitoringLimits.MaximumPendingOutboxPerTenant)
            throw new InvalidOperationException("monitoring_history_capacity_exceeded");
    }
}

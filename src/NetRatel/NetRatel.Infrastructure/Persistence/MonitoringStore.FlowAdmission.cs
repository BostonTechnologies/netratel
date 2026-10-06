using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Infrastructure.Persistence;

public sealed partial class MonitoringStore
{
    /// <summary>Locks current Monitoring authority in the Flow caller's own transaction before its run/action mutation.</summary>
    public static async Task<FlowDispatchDecision> LockAndAdmitFlowAsync(OrchestratorDbContext db, FlowEventEnvelope input,
        Guid expectedRunId, TimeProvider clock, CancellationToken ct)
    {
        if (!FlowContractValidation.ValidEnvelope(input) || expectedRunId == Guid.Empty ||
            !db.Database.IsNpgsql() || db.Database.CurrentTransaction is null) return new(false, "monitoring-admission-invalid");
        var key = $"monitoring:v1:{input.TenantId}:{input.OccurrenceId:D}:{input.EventId:D}:{input.FlowVersionId:D}";
        var candidate = await db.MonitoringFlowOutbox.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == input.TenantId &&
            row.StableFlowDispatchKey == key && row.EventId == input.EventId && row.OccurrenceId == input.OccurrenceId, ct).ConfigureAwait(false);
        if (candidate is null || Deserialize<MonitoringOutboxIntent>(candidate.IntentJson) is not { } intent ||
            MonitoringCanonicalFlowEvent.Create(intent) != input) return new(false, "monitoring-intent-mismatch");
        var configRow = await LockFlowAdmissionConfigurationSnapshotAsync(db, input.TenantId, ct).ConfigureAwait(false);
        var client = new ClientKey(input.TenantId, intent.Series.AgentId);
        var observed = await db.MonitoringEvidenceStreams.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == client.TenantId && row.AgentId == client.AgentId, ct).ConfigureAwait(false);
        if (observed is not { Active: true, CommittedRegistrationOrdinal: > 0 }) return new(false, "monitoring-evidence-unavailable");
        var ownerKey = new OwnerKey(client, observed.ConnectionId, observed.ConnectionEpoch);
        if (await ClientConnectionEpochStore.LockEffectiveOwnerSnapshotAsync(db, ownerKey, clock, ct).ConfigureAwait(false) is null)
            return new(false, "monitoring-owner-unavailable");
        var evidence = await LockFlowAdmissionEvidenceSnapshotAsync(db, client, ct).ConfigureAwait(false);
        var fence = new MonitoringEvidenceFence(client, observed.ConnectionId, observed.ConnectionEpoch, observed.EvidenceStreamId,
            observed.CommittedRegistrationOrdinal);
        if (evidence is not { Active: true } || !MatchesFence(evidence, fence)) return new(false, "monitoring-evidence-changed");
        var series = await LockFlowAdmissionSeriesSnapshotAsync(db, intent.Series, ct).ConfigureAwait(false);
        var outbox = (await db.MonitoringFlowOutbox.FromSqlInterpolated($"""
            SELECT * FROM "MonitoringFlowOutbox" WHERE "TenantId"={input.TenantId} AND "OutboxId"={candidate.OutboxId} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).SingleOrDefault();
        if (outbox is null || outbox.EventId != input.EventId || outbox.OccurrenceId != input.OccurrenceId ||
            outbox.StableFlowDispatchKey != key || outbox.IntentJson != candidate.IntentJson ||
            outbox.FlowRunId is { } knownRun && knownRun != expectedRunId ||
            outbox.Status is not (MonitoringOutboxStatus.Pending or MonitoringOutboxStatus.Leased))
            return new(false, "monitoring-outbox-changed");
        // This is a nonlocking identity read. The caller next locks this exact run using its existing lease boundary.
        var run = await db.FlowRuns.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == input.TenantId && row.Id == expectedRunId, ct).ConfigureAwait(false);
        if (run is null || run.EventId != input.EventId || run.OccurrenceId != input.OccurrenceId || run.FlowVersionId != input.FlowVersionId ||
            JsonSerializer.Deserialize<FlowEventEnvelope>(run.EventJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) != input ||
            run.EventFingerprint != FlowContractValidation.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web)))))
            return new(false, "monitoring-run-mismatch");
        if (!await db.Agents.AsNoTracking().AnyAsync(agent => agent.TenantId == client.TenantId && agent.Id == client.AgentId &&
            agent.IsEnabled && agent.Status == AgentStatus.Active && agent.RevokedAtUtc == null && agent.DeletedAtUtc == null && agent.SupersededAtUtc == null, ct).ConfigureAwait(false))
            return new(false, "monitoring-target-unavailable");
        var configuration = await ReadFlowAdmissionConfigurationAsync(db, input.TenantId, configRow.Revision, clock, ct).ConfigureAwait(false);
        var rule = configuration.Rules.SingleOrDefault(item => item.TenantId == input.TenantId && item.RuleId == intent.Series.RuleId);
        if (rule is null || !Applicable(rule, client.AgentId, configuration, [client.AgentId])) return new(false, "monitoring-target-removed");
        if (!await (from version in db.FlowVersions.AsNoTracking()
            join definition in db.FlowDefinitions.AsNoTracking() on new { version.TenantId, Id = version.FlowId } equals new { definition.TenantId, definition.Id }
            where version.TenantId == input.TenantId && version.Id == input.FlowVersionId && definition.Enabled
            select version.Id).AnyAsync(ct).ConfigureAwait(false)) return new(false, "monitoring-published-flow-unavailable");
        var currentOwner = await ClientConnectionEpochStore.LockEffectiveOwnerSnapshotAsync(db, ownerKey, clock, ct).ConfigureAwait(false);
        if (currentOwner is null) return new(false, "monitoring-owner-expired");
        var now = await EvidenceNowAsync(db, clock, ct).ConfigureAwait(false);
        var state = series is null ? null : ReadState(series);
        if (state is null || state.Cursor?.ConnectionEpoch != ownerKey.Epoch ||
            !new MonitoringSeriesEvaluator(new AdmissionClock(now)).CanDispatch(state, rule, intent, fence.EvidenceStreamId,
                configuration.Bypasses, configuration.Groups)) return new(false, "monitoring-evidence-or-occurrence-unavailable");
        ct.ThrowIfCancellationRequested();
        return new(true, "monitoring-dispatch-admitted");
    }

    // A Flow worker reuses its scoped context before and after awaited preparation.
    // SQL row locks do not refresh an entity already in EF's identity map: a tracked
    // FOR UPDATE query can otherwise return pre-clear StateJson after Clear commits.
    // Admission reads fresh persisted snapshots under the same locks without changing
    // the tracked helpers used by Monitoring mutations or detaching caller-owned state.
    private static async Task<MonitoringTenantConfigurationRecord> LockFlowAdmissionConfigurationSnapshotAsync(
        OrchestratorDbContext db, int tenant, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "MonitoringTenantConfigurations" ("TenantId", "Revision") VALUES ({tenant}, 0) ON CONFLICT ("TenantId") DO NOTHING""", ct).ConfigureAwait(false);
        var rows = await db.MonitoringTenantConfigurations.FromSqlInterpolated($"""
            SELECT * FROM "MonitoringTenantConfigurations" WHERE "TenantId"={tenant} FOR SHARE
            """).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return rows.Single();
    }

    private static async Task<MonitoringEvidenceStreamRecord?> LockFlowAdmissionEvidenceSnapshotAsync(
        OrchestratorDbContext db, ClientKey client, CancellationToken ct) =>
        (await db.MonitoringEvidenceStreams.FromSqlInterpolated($"""
            SELECT * FROM "MonitoringEvidenceStreams" WHERE "TenantId"={client.TenantId} AND "AgentId"={client.AgentId} FOR SHARE
            """).AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).SingleOrDefault();

    private static async Task<MonitoringSeriesRecord?> LockFlowAdmissionSeriesSnapshotAsync(
        OrchestratorDbContext db, MonitoringSeriesKey key, CancellationToken ct) =>
        (await db.MonitoringSeries.FromSqlInterpolated($"""
            SELECT * FROM "MonitoringSeries" WHERE "TenantId"={key.TenantId} AND "RuleId"={key.RuleId}
                AND "AgentId"={key.AgentId} AND "ResourceKey"={key.ResourceKey} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).SingleOrDefault();

    private static async Task<MonitoringConfigurationSnapshot> ReadFlowAdmissionConfigurationAsync(OrchestratorDbContext db,
        int tenant, ulong revision, TimeProvider clock, CancellationToken ct)
    {
        var rules = await db.MonitoringRules.AsNoTracking().Where(row => row.TenantId == tenant).OrderBy(row => row.RuleId)
            .Take(MonitoringLimits.MaximumRulesPerTenant + 1).Select(row => row.DefinitionJson).ToListAsync(ct).ConfigureAwait(false);
        var groups = await db.MonitoringGroups.AsNoTracking().Where(row => row.TenantId == tenant).OrderBy(row => row.GroupId)
            .Take(MonitoringLimits.MaximumGroupsPerTenant + 1).Select(row => row.DefinitionJson).ToListAsync(ct).ConfigureAwait(false);
        var bypasses = await db.MonitoringBypasses.AsNoTracking().Where(row => row.TenantId == tenant).OrderBy(row => row.BypassId)
            .Take(MonitoringLimits.MaximumBypassesPerEvaluation + 1).Select(row => row.DefinitionJson).ToListAsync(ct).ConfigureAwait(false);
        if (rules.Count > MonitoringLimits.MaximumRulesPerTenant || groups.Count > MonitoringLimits.MaximumGroupsPerTenant ||
            bypasses.Count > MonitoringLimits.MaximumBypassesPerEvaluation) throw new InvalidOperationException("configuration_capacity_exceeded");
        var config = new MonitoringConfigurationSnapshot(tenant, revision, rules.Select(Deserialize<MonitoringRuleDto>).ToImmutableArray(),
            groups.Select(Deserialize<MonitoringGroupDto>).ToImmutableArray(), bypasses.Select(Deserialize<MonitoringBypassDto>).ToImmutableArray(), clock.GetUtcNow());
        RequireConfigurationSize(config);
        return config;
    }

    private sealed class AdmissionClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
}

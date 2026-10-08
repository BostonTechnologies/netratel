using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>All operations create their own DbContext. Locks and row revisions provide durable fences, not actor HA.</summary>
public sealed partial class MonitoringStore(IServiceScopeFactory scopeFactory, TimeProvider timeProvider,
    IMonitoringPublishedFlowProvider publishedFlows, IMonitoringClientDirectory directory)
    : IMonitoringStore, IMonitoringConfigurationStore
{
    private const int MaximumAggregateBytes = 4 * 1024 * 1024;
    private const int MaximumSeriesPerTenant = 16_384;
    private const int ReconciliationBatchSize = 64;
    private readonly MonitoringSeriesEvaluator _evaluator = new(timeProvider);
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 32 };

    public async Task<MonitoringSeriesState?> LoadSeriesAsync(MonitoringSeriesKey series, CancellationToken cancellationToken)
    {
        RequireSeries(series);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var row = await SeriesQuery(db, series).AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : ReadState(row);
    }

    public async Task<ImmutableArray<MonitoringSeriesState>> LoadClientAsync(ClientKey client, CancellationToken cancellationToken)
    {
        if (!client.IsValid) throw new ArgumentException("invalid_client");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var rows = await db.MonitoringSeries.AsNoTracking().Where(row => row.TenantId == client.TenantId && row.AgentId == client.AgentId)
            .OrderBy(row => row.RuleId).ThenBy(row => row.ResourceKey).Take(MonitoringLimits.MaximumSeriesPerClient + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count > MonitoringLimits.MaximumSeriesPerClient) throw new InvalidOperationException("series_capacity_exceeded");
        if (rows.Sum(row => (long)Encoding.UTF8.GetByteCount(row.StateJson)) > MaximumAggregateBytes - 1024)
            throw new InvalidOperationException("monitoring_client_state_size_exceeded");
        return rows.Select(ReadState).ToImmutableArray();
    }

    public async Task<MonitoringSeriesPageDto> ReadTenantSeriesAsync(int tenantId, int maximumCount, string? cursor, CancellationToken cancellationToken)
    {
        RequireRead(tenantId, maximumCount);
        var last = DecodeCursor<SeriesCursor>(cursor);
        if (last is not null && last.TenantId != tenantId) throw new ArgumentException("foreign_cursor");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var query = last is null ? db.MonitoringSeries.Where(row => row.TenantId == tenantId) :
            db.MonitoringSeries.FromSqlInterpolated($"""
                SELECT * FROM "MonitoringSeries" WHERE "TenantId" = {tenantId}
                AND ("RuleId", "AgentId", "ResourceKey") > ({last.RuleId}, {last.AgentId}, {last.ResourceKey})
                """);
        var rows = await query.AsNoTracking().OrderBy(row => row.RuleId).ThenBy(row => row.AgentId).ThenBy(row => row.ResourceKey)
            .Take(maximumCount + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = BudgetPage(rows.Take(maximumCount), row => ReadState(row));
        return new(page.Select(ReadState).ToImmutableArray(), rows.Count > page.Length && page.Length > 0
            ? EncodeCursor(new SeriesCursor(tenantId, page[^1].RuleId, page[^1].AgentId, page[^1].ResourceKey)) : null);
    }

    public async Task<MonitoringEventPageDto> ReadTenantEventsAsync(int tenantId, int maximumCount, string? cursor, CancellationToken cancellationToken)
    {
        RequireRead(tenantId, maximumCount);
        var last = DecodeCursor<EventCursor>(cursor);
        if (last is not null && last.TenantId != tenantId) throw new ArgumentException("foreign_cursor");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        // Merge the existing durable event/audit projections before paging. Series audits are represented
        // by their occurrence events; configuration/bypass audits retain their own immutable attribution.
        var candidates = await db.Database.SqlQuery<HistoryRow>($"""
            SELECT "AtUtc", "ItemId", "IsAudit" FROM (
                SELECT "AtUtc", "EventId" AS "ItemId", FALSE AS "IsAudit" FROM "MonitoringEvents" WHERE "TenantId" = {tenantId}
                UNION ALL
                SELECT "AtUtc", "AuditId" AS "ItemId", TRUE AS "IsAudit" FROM "MonitoringAudits" WHERE "TenantId" = {tenantId} AND "EntityKind" <> 'series'
            ) history WHERE ({last == null} OR ("AtUtc", "ItemId") < ({(last == null ? DateTimeOffset.MaxValue : last.AtUtc)}, {(last == null ? Guid.Empty : last.EventId)}))
            ORDER BY "AtUtc" DESC, "ItemId" DESC LIMIT {maximumCount + 1}
            """).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var page = candidates.Take(maximumCount).ToArray();
        var eventIds = page.Where(row => !row.IsAudit).Select(row => row.ItemId).ToArray();
        var auditIds = page.Where(row => row.IsAudit).Select(row => row.ItemId).ToArray();
        var events = await db.MonitoringEvents.AsNoTracking().Where(row => row.TenantId == tenantId && eventIds.Contains(row.EventId))
            .OrderByDescending(row => row.AtUtc).ThenByDescending(row => row.EventId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var audits = await db.MonitoringAudits.AsNoTracking().Where(row => row.TenantId == tenantId && auditIds.Contains(row.AuditId))
            .OrderByDescending(row => row.AtUtc).ThenByDescending(row => row.AuditId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var items = events.Select(row => Deserialize<MonitoringEventIntent>(row.EventJson)).ToImmutableArray();
        var historyAudits = audits.Select(row => new MonitoringHistoryAuditDto(row.AuditId, row.Operation, row.EntityId, row.EntityKind,
            ReadHistoryDetail(row.DetailsJson, "EntityName"), row.OperatorId, ReadHistoryDetail(row.DetailsJson, "OperatorDisplayName"), row.Reason, row.AtUtc)).ToImmutableArray();
        var eventMap = items.ToDictionary(item => item.EventId);
        var auditMap = historyAudits.ToDictionary(item => item.AuditId);
        var bounded = BudgetPage(page, row => row.IsAudit ? (object)auditMap[row.ItemId] : eventMap[row.ItemId]);
        var retained = bounded.Select(row => row.ItemId).ToHashSet();
        return new(items.Where(item => retained.Contains(item.EventId)).ToImmutableArray(), candidates.Length > bounded.Length && bounded.Length > 0
            ? EncodeCursor(new EventCursor(tenantId, bounded[^1].AtUtc, bounded[^1].ItemId)) : null,
            Audits: historyAudits.Where(item => retained.Contains(item.AuditId)).ToImmutableArray());
    }

    public async Task<MonitoringSummaryDto> ReadTenantSummaryAsync(int tenantId, CancellationToken cancellationToken)
    {
        RequireTenant(tenantId);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var query = db.MonitoringSeries.Where(row => row.TenantId == tenantId);
        return new(tenantId, await query.LongCountAsync(cancellationToken),
            await query.LongCountAsync(row => row.ActiveOccurrenceId != null, cancellationToken),
            await query.LongCountAsync(row => row.Phase == MonitoringPhase.Pending, cancellationToken),
            await query.LongCountAsync(row => row.EvidenceQuality == MonitoringEvidenceQuality.Unknown, cancellationToken),
            await query.LongCountAsync(row => row.ActiveOccurrenceId != null && row.Acknowledged, cancellationToken),
            await query.LongCountAsync(row => row.ActiveOccurrenceId != null && row.Suppressed, cancellationToken), timeProvider.GetUtcNow(),
            await query.LongCountAsync(row => row.ActiveOccurrenceId != null && row.Phase == MonitoringPhase.Firing, cancellationToken));
    }

    private static async Task<ImmutableArray<Guid>> EligibleAsync(OrchestratorDbContext db, int tenantId, CancellationToken ct)
    {
        var ids = await db.Agents.AsNoTracking().Where(agent => agent.TenantId == tenantId && agent.IsEnabled && agent.Status == AgentStatus.Active &&
            agent.RevokedAtUtc == null && agent.DeletedAtUtc == null && agent.SupersededAtUtc == null).OrderBy(agent => agent.Id)
            .Select(agent => agent.Id).Take(MonitoringLimits.MaximumTargetClients + 1).ToArrayAsync(ct).ConfigureAwait(false);
        if (ids.Length > MonitoringLimits.MaximumTargetClients) throw new InvalidOperationException("target_capacity_exceeded");
        return ids.ToImmutableArray();
    }

    private static IQueryable<MonitoringSeriesRecord> SeriesQuery(OrchestratorDbContext db, MonitoringSeriesKey key) =>
        db.MonitoringSeries.Where(row => row.TenantId == key.TenantId && row.RuleId == key.RuleId && row.AgentId == key.AgentId && row.ResourceKey == key.ResourceKey);

    private static MonitoringSeriesState ReadState(MonitoringSeriesRecord row)
    {
        var state = Deserialize<MonitoringSeriesState>(row.StateJson);
        if (state.Series != new MonitoringSeriesKey(row.TenantId, row.RuleId, row.AgentId, row.ResourceKey) || state.StateRevision != row.StateRevision)
            throw new InvalidOperationException("inconsistent_monitoring_state");
        return state;
    }

    private static void ApplyState(MonitoringSeriesRecord row, MonitoringSeriesState state, DateTimeOffset now)
    {
        row.StateRevision = state.StateRevision; row.StateJson = Serialize(state); row.UpdatedAtUtc = now;
        row.Phase = state.Phase; row.EvidenceQuality = state.EvidenceQuality; row.Suppressed = state.Suppressed;
        row.ActiveOccurrenceId = state.Occurrence is { EndedAtUtc: null } open ? open.OccurrenceId : null;
        row.LatestOccurrenceId = state.Occurrence?.OccurrenceId;
        row.Acknowledged = state.Occurrence?.AcknowledgedAtUtc is not null;
    }

    private static string Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > 1024 * 1024) throw new ArgumentException("monitoring_record_size_exceeded");
        return json;
    }
    private static T[] BudgetPage<T, TDto>(IEnumerable<T> rows, Func<T, TDto> project)
    {
        var result = new List<T>();
        var size = 1024; // Envelope, cursor, separators, and application JSON naming overhead.
        foreach (var row in rows)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(project(row), JsonOptions).Length;
            if (bytes + 1024 > MaximumAggregateBytes) throw new InvalidOperationException("monitoring_row_size_exceeded");
            if (size + bytes + 1 > MaximumAggregateBytes) break;
            result.Add(row); size += bytes + 1;
        }
        return result.ToArray();
    }
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException("invalid_monitoring_record");
    private static string EncodeCursor<T>(T cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(Serialize(cursor)));
    private static T? DecodeCursor<T>(string? cursor) where T : class
    {
        if (cursor is null) return null;
        if (cursor.Length > 4096) throw new ArgumentException("invalid_cursor");
        try { return Deserialize<T>(Encoding.UTF8.GetString(Convert.FromBase64String(cursor))); }
        catch (Exception exception) when (exception is FormatException or JsonException) { throw new ArgumentException("invalid_cursor", exception); }
    }
    private static void RequireTenant(int tenant) { if (tenant <= 0) throw new ArgumentException("invalid_tenant"); }
    private static void RequireRead(int tenant, int count) { RequireTenant(tenant); if (count is < 1 or > MonitoringLimits.MaximumRowsPerRead) throw new ArgumentException("invalid_page_size"); }
    private static void RequireSeries(MonitoringSeriesKey key)
    {
        RequireTenant(key.TenantId);
        if (key.RuleId == Guid.Empty || key.AgentId == Guid.Empty || string.IsNullOrWhiteSpace(key.ResourceKey) ||
            key.ResourceKey.Length > MonitoringLimits.MaximumResourceKeyLength || key.ResourceKey.Any(char.IsControl)) throw new ArgumentException("invalid_series");
    }
    private static string? ReadHistoryDetail(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private sealed record HistoryRow(DateTimeOffset AtUtc, Guid ItemId, bool IsAudit);
    private sealed record SeriesCursor(int TenantId, Guid RuleId, Guid AgentId, string ResourceKey);
    private sealed record EventCursor(int TenantId, DateTimeOffset AtUtc, Guid EventId);
}

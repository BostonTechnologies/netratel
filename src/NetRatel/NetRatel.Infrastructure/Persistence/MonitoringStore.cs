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

    public Task<MonitoringSeriesPageDto> ReadTenantSeriesAsync(int tenantId, int maximumCount, string? cursor, CancellationToken cancellationToken) =>
        ReadTenantSeriesAsync(new MonitoringQueryDto(tenantId, MaximumCount: maximumCount), cursor, cancellationToken);

    public Task<MonitoringEventPageDto> ReadTenantEventsAsync(int tenantId, int maximumCount, string? cursor, CancellationToken cancellationToken) =>
        ReadTenantEventsAsync(new MonitoringQueryDto(tenantId, MaximumCount: maximumCount), cursor, cancellationToken);

    public async Task<MonitoringSummaryDto> ReadTenantSummaryAsync(int tenantId, CancellationToken cancellationToken)
    {
        RequireTenant(tenantId);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var query = db.MonitoringSeries.Where(row => row.TenantId == tenantId);
        var offlineClients = await db.Database.SqlQuery<long>($"""
            SELECT COUNT(DISTINCT member."ClientId")::bigint AS "Value" FROM "MonitoringSeries"
            CROSS JOIN LATERAL jsonb_array_elements_text(COALESCE("StateJson" -> 'OfflineAgentIds', '[]'::jsonb)) AS member("ClientId")
            WHERE "TenantId" = {tenantId} AND "AgentId" = {Guid.Empty} AND "ResourceKey" = {MonitoringOfflinePresentation.ResourceKey}
                AND "Phase" <> {(short)MonitoringPhase.Suspended} AND "Phase" <> {(short)MonitoringPhase.NotApplicable}
            """).SingleAsync(cancellationToken).ConfigureAwait(false);
        return new(tenantId, await query.LongCountAsync(cancellationToken),
            await query.LongCountAsync(row => row.ActiveOccurrenceId != null, cancellationToken),
            await query.LongCountAsync(row => row.Phase == MonitoringPhase.Pending, cancellationToken),
            await query.LongCountAsync(row => row.EvidenceQuality == MonitoringEvidenceQuality.Unknown, cancellationToken),
            await query.LongCountAsync(row => row.ActiveOccurrenceId != null && row.Acknowledged, cancellationToken),
            await query.LongCountAsync(row => row.ActiveOccurrenceId != null && row.Suppressed, cancellationToken), timeProvider.GetUtcNow(),
            await query.LongCountAsync(row => row.ActiveOccurrenceId != null && row.Phase == MonitoringPhase.Firing, cancellationToken), offlineClients);
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
        var size = 4096 + MonitoringQueryLimits.MaximumPageIdentityBytes; // Envelope, cursor and the bounded directory projection.
        foreach (var row in rows)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(project(row), JsonOptions).Length;
            if (bytes + 4096 + MonitoringQueryLimits.MaximumPageIdentityBytes > MaximumAggregateBytes) throw new InvalidOperationException("monitoring_row_size_exceeded");
            if (size + bytes + 1 > MaximumAggregateBytes) break;
            result.Add(row); size += bytes + 1;
        }
        return result.ToArray();
    }
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException("invalid_monitoring_record");
    private static void RequireTenant(int tenant) { if (tenant <= 0) throw new ArgumentException("invalid_tenant"); }
    private static void RequireRead(int tenant, int count) { RequireTenant(tenant); if (count is < 1 or > MonitoringLimits.MaximumRowsPerRead) throw new ArgumentException("invalid_page_size"); }
    private static void RequireSeries(MonitoringSeriesKey key)
    {
        RequireTenant(key.TenantId);
        if (key.RuleId == Guid.Empty || key.AgentId == Guid.Empty && !MonitoringOfflinePresentation.IsSeries(key) || string.IsNullOrWhiteSpace(key.ResourceKey) ||
            key.ResourceKey.Length > MonitoringLimits.MaximumResourceKeyLength || key.ResourceKey.Any(char.IsControl)) throw new ArgumentException("invalid_series");
    }
    private static string? ReadHistoryDetail(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private sealed record HistoryRow(DateTimeOffset AtUtc, Guid ItemId, bool IsAudit);
    private sealed record SeriesCursor(int TenantId, Guid RuleId, Guid AgentId, string ResourceKey);

}

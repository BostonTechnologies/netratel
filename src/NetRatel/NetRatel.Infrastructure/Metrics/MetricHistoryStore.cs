using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.Infrastructure.Clients;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;
using Npgsql;

namespace NetRatel.Infrastructure.Metrics;

public sealed partial class MetricHistoryStore(OrchestratorDbContext db, TimeProvider time)
{
    public const long TenantRowLimit = 120_000_000;
    public const long InstanceRowLimit = 480_000_000;
    private const int MaximumTenantSeries = 32_768;
    private const int MaximumClientSeries = 320;
    private const int HistoryLockNamespace = 0x4E524D48;
    private const int RollupHeadroomPercent = 25;
    private static readonly TimeSpan MaximumBacklogAge = TimeSpan.FromDays(2);
    private readonly OrchestratorDbContext _db = db;

    internal async Task PersistAsync(int tenant, Guid batchId, IReadOnlyList<MetricIngress> batch, CancellationToken ct)
    {
        _db.Database.SetCommandTimeout(8);
        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await TryLockAsync(tenant, ct).ConfigureAwait(false)) throw new MetricHistoryBusyException();
        if (await _db.Set<MetricReceiptRow>().AnyAsync(item => item.TenantId == tenant && item.BatchId == batchId, ct).ConfigureAwait(false))
            return;

        if (!await _db.Tenants.AnyAsync(item => item.Id == tenant, ct).ConfigureAwait(false)) return;
        var policy = await GetOrAddPolicyAsync(tenant, ct).ConfigureAwait(false);
        if (!policy.Enabled) return;
        var now = time.GetUtcNow();
        var oldestUnrolled = await OldestBucketAsync(tenant, 60, policy.FiveSealedBefore, ct).ConfigureAwait(false);
        if (oldestUnrolled < now - MaximumBacklogAge || policy.RetainedRows >= IngestLimit(TenantRowLimit))
            throw new MetricHistoryCapacityException("maintenance_backlog");

        var agents = batch.Select(input => input.Fence.Client.AgentId).Distinct().ToArray();
        var targets = await _db.Agents.AsNoTracking()
            .Where(agent => agent.TenantId == tenant && agents.Contains(agent.Id) && agent.IsEnabled && agent.Status == NetRatel.Infrastructure.Persistence.AgentStatus.Active && agent.RevokedAtUtc == null)
            .Select(agent => new { agent.Id, agent.DeviceInfoJson })
            .ToDictionaryAsync(agent => agent.Id, agent => IsWindows(ClientDirectoryIdentity.Parse(agent.DeviceInfoJson).OperatingSystem), ct).ConfigureAwait(false);
        var registered = await _db.Set<MetricSeriesRow>().AsTracking()
            .Where(row => row.TenantId == tenant && agents.Contains(row.AgentId))
            .ToListAsync(ct).ConfigureAwait(false);
        var seriesByKey = registered.ToDictionary(row => (row.AgentId, row.Kind, row.Resource));
        var counts = registered.GroupBy(row => row.AgentId).ToDictionary(group => group.Key, group => group.Count());
        var accumulator = new MetricMinuteAccumulator();
        var sealedBefore = MetricMinuteAccumulator.Max(policy.FiveSealedBefore.GetValueOrDefault(),
            MetricMinuteAccumulator.Floor(now - MetricMinuteAccumulator.Lateness, 60));
        var exhausted = false;
        foreach (var input in batch)
        {
            var agentId = input.Fence.Client.AgentId;
            if (!targets.TryGetValue(agentId, out var windows)) continue;
            policy.LastAcceptedAt = MetricMinuteAccumulator.Max(policy.LastAcceptedAt.GetValueOrDefault(), input.ReceivedAt);
            if (input.Partial) policy.PartialFrames++;
            var readings = input.Snapshot is { } snapshot ? MetricHistoryObservations.Read(snapshot, windows || IsWindows(snapshot.TransportHealth?.OsVersion)) : input.Watches!;
            foreach (var reading in readings)
            {
                var key = (agentId, reading.Kind, reading.Key);
                var added = !seriesByKey.TryGetValue(key, out var series);
                if (added)
                {
                    if (policy.SeriesCount >= MaximumTenantSeries || counts.GetValueOrDefault(agentId) >= MaximumClientSeries)
                    {
                        exhausted = true;
                        continue;
                    }
                    series = new() { TenantId = tenant, AgentId = agentId, Kind = reading.Kind, Resource = reading.Key,
                        Label = reading.Label, FirstAt = reading.OrderAt };
                }
                if (accumulator.Accept(series!, reading, input, sealedBefore, policy.EnabledSince) && added)
                {
                    seriesByKey.Add(key, series!);
                    _db.Add(series!);
                    policy.SeriesCount++;
                    counts[agentId] = counts.GetValueOrDefault(agentId) + 1;
                }
            }
        }

        policy.LateObservations += accumulator.Late;
        policy.Disposition = exhausted ? "series_capacity" : accumulator.Late > 0 ? "late_observation" : null;
        _db.Add(new MetricReceiptRow { TenantId = tenant, BatchId = batchId, CreatedAt = now });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        accumulator.BindSeriesIds();
        var rows = await WriteBucketsAsync(tenant, accumulator.Buckets.ToArray(), ct).ConfigureAwait(false);
        rows += 1 + await WriteDetailsAsync(tenant, accumulator.Details, ct).ConfigureAwait(false);
        if (rows > 0 && (policy.RetainedRows + rows > IngestLimit(TenantRowLimit) || !await AdjustInstanceAsync(rows, true, ct).ConfigureAwait(false)))
            throw new MetricHistoryCapacityException("row_budget");
        policy.RetainedRows += rows;
        if (accumulator.Buckets.Any()) policy.LastPersistedAt = now;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    internal async Task<DateTimeOffset?> OldestBucketAsync(int tenant, short tier, DateTimeOffset? from, CancellationToken ct) =>
        await _db.Set<MetricBucketRow>().AsNoTracking()
            .Where(row => row.TenantId == tenant && row.Tier == tier && (from == null || row.Bucket >= from))
            .OrderBy(row => row.Bucket).Select(row => (DateTimeOffset?)row.Bucket).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    internal async Task<MetricPolicyRow> GetOrAddPolicyAsync(int tenant, CancellationToken ct)
    {
        var row = await _db.Set<MetricPolicyRow>().AsTracking().SingleOrDefaultAsync(item => item.TenantId == tenant, ct).ConfigureAwait(false);
        if (row is not null) return row;
        row = new() { TenantId = tenant };
        _db.Add(row);
        return row;
    }

    internal Task<bool> TryLockAsync(int tenant, CancellationToken ct) => _db.Database
        .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({HistoryLockNamespace}, {tenant}) AS \"Value\"")
        .SingleAsync(ct);

    private async Task<bool> AdjustInstanceAsync(long rows, bool ingest, CancellationToken ct)
    {
        if (rows == 0) return true;
        var limit = ingest ? IngestLimit(InstanceRowLimit) : InstanceRowLimit;
        var changed = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MetricInstanceBudgets" SET "RetainedRows" = "RetainedRows" + {rows}
            WHERE "Id" = 1 AND "RetainedRows" + {rows} BETWEEN 0 AND {limit}
            """, ct).ConfigureAwait(false);
        return changed == 1;
    }

    private static long IngestLimit(long limit) => limit * (100 - RollupHeadroomPercent) / 100;

    private static bool IsWindows(string? operatingSystem) => operatingSystem?.Contains("windows", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<NpgsqlCommand> CommandAsync(string sql, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await _db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        return new(sql, connection, (NpgsqlTransaction?)_db.Database.CurrentTransaction?.GetDbTransaction())
        {
            CommandTimeout = _db.Database.GetCommandTimeout() ?? 8
        };
    }

    private static string? ExactBytes(decimal? bytes) => bytes?.ToString("0", CultureInfo.InvariantCulture);
}

public sealed class MetricHistoryBusyException : Exception;
internal sealed class MetricHistoryCapacityException(string disposition) : Exception(disposition);

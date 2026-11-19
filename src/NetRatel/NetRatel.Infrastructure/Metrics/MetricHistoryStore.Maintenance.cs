using Microsoft.EntityFrameworkCore;
using static NetRatel.Infrastructure.Metrics.MetricMinuteAccumulator;

namespace NetRatel.Infrastructure.Metrics;

public sealed partial class MetricHistoryStore
{
    private const int PurgeBatch = 5_000;
    private const int CleanupBatch = 512;

    internal Task<List<int>> MaintenanceTenantsAsync(int after, CancellationToken ct) => _db.Set<MetricPolicyRow>().AsNoTracking()
        .Where(item => item.TenantId > after).OrderBy(item => item.TenantId).Select(item => item.TenantId).Take(32).ToListAsync(ct);

    internal async Task<bool> MaintainAsync(int tenant, CancellationToken ct)
    {
        _db.Database.SetCommandTimeout(6);
        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await TryLockAsync(tenant, ct).ConfigureAwait(false)) return false;
        var policy = await _db.Set<MetricPolicyRow>().AsTracking().SingleOrDefaultAsync(row => row.TenantId == tenant, ct).ConfigureAwait(false);
        if (policy is null) return false;
        var now = time.GetUtcNow();

        var initialRows = policy.RetainedRows;
        var deleted = await PurgeAsync(policy, now, ct).ConfigureAwait(false);
        policy.FiveSealedBefore = await AdvanceTierAsync(policy, 60, 300, policy.FiveSealedBefore, Floor(now - Lateness, 300), ct)
            .ConfigureAwait(false);
        policy.HourSealedBefore = await AdvanceTierAsync(policy, 300, 3600, policy.HourSealedBefore,
            Floor(policy.FiveSealedBefore.Value, 3600), ct).ConfigureAwait(false);
        policy.LastRollupAt = now;
        policy.LastPurgeAt = now;
        if (deleted > 0 || policy.RetainedRows == 0) await RemoveUnusedSeriesAsync(policy, now, ct).ConfigureAwait(false);

        if (policy.RetainedRows == 0 && policy.SeriesCount == 0 &&
            !await _db.Tenants.AnyAsync(row => row.Id == tenant, ct).ConfigureAwait(false))
        {
            await _db.Set<MetricWatchRow>().Where(row => row.TenantId == tenant).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            _db.Remove(policy);
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (!await AdjustInstanceAsync(policy.RetainedRows - initialRows, false, ct).ConfigureAwait(false))
            throw new MetricHistoryCapacityException("rollup_budget");
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task<DateTimeOffset> AdvanceTierAsync(MetricPolicyRow policy, short source, short target,
        DateTimeOffset? watermark, DateTimeOffset limit, CancellationToken ct)
    {
        if (watermark >= limit) return watermark.Value;
        var oldest = await OldestBucketAsync(policy.TenantId, source, watermark, ct).ConfigureAwait(false);
        if (oldest is null || oldest >= limit) return limit;
        var start = Floor(oldest.Value, target);
        var end = start.AddSeconds(target);
        var added = await RollupAsync(policy.TenantId, source, target, start, end, ct).ConfigureAwait(false);
        if (policy.RetainedRows + added > TenantRowLimit)
            throw new MetricHistoryCapacityException("rollup_budget");
        policy.RetainedRows += added;
        return end;
    }

    private async Task<int> RollupAsync(int tenant, short source, short target, DateTimeOffset start, DateTimeOffset end, CancellationToken ct) =>
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MetricBuckets" ("TenantId", "SeriesId", "Tier", "Bucket", "Count", "Min", "Max", "Last", "LastAt",
                "Integral", "Covered", "Bytes", "TotalBytes", "FreeBytes", "Quality", "Running", "Stopped", "Other")
            SELECT {tenant}, "SeriesId", {target}, {start}, sum("Count"), min("Min"), max("Max"),
                (array_agg("Last" ORDER BY "LastAt" DESC NULLS LAST, "Bucket" DESC))[1], max("LastAt"),
                sum("Integral"), sum("Covered"), sum("Bytes"),
                (array_agg("TotalBytes" ORDER BY "LastAt" DESC NULLS LAST, "Bucket" DESC))[1],
                (array_agg("FreeBytes" ORDER BY "LastAt" DESC NULLS LAST, "Bucket" DESC))[1],
                bit_or("Quality"), sum("Running"), sum("Stopped"), sum("Other")
            FROM "MetricBuckets"
            WHERE "TenantId" = {tenant} AND "Tier" = {source} AND "Bucket" >= {start} AND "Bucket" < {end}
            GROUP BY "SeriesId"
            ON CONFLICT ("TenantId", "SeriesId", "Tier", "Bucket") DO NOTHING
            """, ct).ConfigureAwait(false);

    private async Task<long> PurgeAsync(MetricPolicyRow policy, DateTimeOffset now, CancellationToken ct)
    {
        var minuteCutoff = Min(Floor(now.AddDays(-policy.BaseDays), 60), policy.FiveSealedBefore.GetValueOrDefault());
        var fiveCutoff = Min(Floor(now.AddDays(-policy.FiveMinuteDays), 300), policy.HourSealedBefore.GetValueOrDefault());
        var deleted = await PurgeTierAsync(policy.TenantId, 60, minuteCutoff, ct).ConfigureAwait(false);
        if (minuteCutoff != default) policy.BasePurgedBefore = Max(policy.BasePurgedBefore.GetValueOrDefault(), minuteCutoff);
        if (fiveCutoff != default) policy.FivePurgedBefore = Max(policy.FivePurgedBefore.GetValueOrDefault(), fiveCutoff);
        deleted += await PurgeTierAsync(policy.TenantId, 300, fiveCutoff, ct).ConfigureAwait(false);
        deleted += await PurgeTierAsync(policy.TenantId, 3600, Floor(now.AddDays(-policy.HourlyDays), 3600), ct).ConfigureAwait(false);
        deleted += await _db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "MetricServiceDetails" AS detail USING (
                SELECT "SeriesId", "ObservedAt" FROM "MetricServiceDetails"
                WHERE "TenantId" = {policy.TenantId} AND "ObservedAt" < {minuteCutoff}
                ORDER BY "ObservedAt" LIMIT {PurgeBatch}
            ) expired WHERE detail."TenantId" = {policy.TenantId} AND detail."SeriesId" = expired."SeriesId"
                AND detail."ObservedAt" = expired."ObservedAt"
            """, ct).ConfigureAwait(false);
        var receiptCutoff = now.AddMinutes(-15);
        deleted += await _db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "MetricReceipts" AS receipt USING (
                SELECT "BatchId" FROM "MetricReceipts"
                WHERE "TenantId" = {policy.TenantId} AND "CreatedAt" < {receiptCutoff}
                ORDER BY "CreatedAt" LIMIT {CleanupBatch}
            ) expired WHERE receipt."TenantId" = {policy.TenantId} AND receipt."BatchId" = expired."BatchId"
            """, ct).ConfigureAwait(false);
        policy.RetainedRows -= deleted;
        return deleted;
    }

    private Task<int> PurgeTierAsync(int tenant, short tier, DateTimeOffset cutoff, CancellationToken ct) =>
        _db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "MetricBuckets" AS bucket USING (
                SELECT "SeriesId", "Bucket" FROM "MetricBuckets"
                WHERE "TenantId" = {tenant} AND "Tier" = {tier} AND "Bucket" < {cutoff}
                ORDER BY "Bucket", "SeriesId" LIMIT {PurgeBatch}
            ) expired WHERE bucket."TenantId" = {tenant} AND bucket."Tier" = {tier}
                AND bucket."SeriesId" = expired."SeriesId" AND bucket."Bucket" = expired."Bucket"
            """, ct);

    private async Task RemoveUnusedSeriesAsync(MetricPolicyRow policy, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-1);
        var deleted = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "MetricSeries" WHERE "TenantId" = {policy.TenantId} AND "Id" IN (
                SELECT s."Id" FROM "MetricSeries" s WHERE s."TenantId" = {policy.TenantId} AND s."LastAt" < {cutoff}
                  AND NOT EXISTS (SELECT 1 FROM "MetricBuckets" b WHERE b."TenantId" = s."TenantId" AND b."SeriesId" = s."Id")
                  AND NOT EXISTS (SELECT 1 FROM "MetricServiceDetails" d WHERE d."TenantId" = s."TenantId" AND d."SeriesId" = s."Id")
                ORDER BY s."Id" LIMIT {CleanupBatch}
            )
            """, ct).ConfigureAwait(false);
        policy.SeriesCount -= deleted;
    }
}

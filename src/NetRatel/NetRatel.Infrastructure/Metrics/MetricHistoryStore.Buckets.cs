using Npgsql;
using NpgsqlTypes;

namespace NetRatel.Infrastructure.Metrics;

public sealed partial class MetricHistoryStore
{
    private async Task<long> WriteBucketsAsync(int tenant, MetricBucketRow[] buckets, CancellationToken ct)
    {
        if (buckets.Length == 0) return 0;
        await using var command = await CommandAsync(UpsertBuckets, ct).ConfigureAwait(false);
        command.Parameters.AddWithValue("tenant", tenant);
        Bind("series", NpgsqlDbType.Bigint, row => row.SeriesId);
        Bind("buckets", NpgsqlDbType.TimestampTz, row => row.Bucket);
        Bind("counts", NpgsqlDbType.Bigint, row => row.Count);
        Bind("mins", NpgsqlDbType.Double, row => row.Min);
        Bind("maxs", NpgsqlDbType.Double, row => row.Max);
        Bind("lasts", NpgsqlDbType.Double, row => row.Last);
        Bind("last_times", NpgsqlDbType.TimestampTz, row => row.LastAt);
        Bind("integrals", NpgsqlDbType.Double, row => row.Integral);
        Bind("covered", NpgsqlDbType.Bigint, row => row.Covered);
        Bind("bytes", NpgsqlDbType.Numeric, row => row.Bytes);
        Bind("totals", NpgsqlDbType.Numeric, row => row.TotalBytes);
        Bind("free", NpgsqlDbType.Numeric, row => row.FreeBytes);
        Bind("qualities", NpgsqlDbType.Integer, row => (int)row.Quality);
        Bind("running", NpgsqlDbType.Bigint, row => row.Running);
        Bind("stopped", NpgsqlDbType.Bigint, row => row.Stopped);
        Bind("other", NpgsqlDbType.Bigint, row => row.Other);
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

        void Bind<T>(string name, NpgsqlDbType type, Func<MetricBucketRow, T> select) =>
            command.Parameters.AddWithValue(name, NpgsqlDbType.Array | type, buckets.Select(select).ToArray());
    }

    private async Task<long> WriteDetailsAsync(int tenant,
        List<(MetricSeriesRow Series, DateTimeOffset At, NetRatel.Shared.Contracts.Services.ClientServiceState State)> details, CancellationToken ct)
    {
        if (details.Count == 0) return 0;
        await using var command = await CommandAsync("""
            INSERT INTO "MetricServiceDetails" ("TenantId", "SeriesId", "ObservedAt", "State")
            SELECT @tenant, detail.* FROM unnest(@series, @observed, @states) detail
            ON CONFLICT DO NOTHING
            """, ct).ConfigureAwait(false);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("series", details.Select(detail => detail.Series.Id).ToArray());
        command.Parameters.AddWithValue("observed", details.Select(detail => detail.At).ToArray());
        command.Parameters.AddWithValue("states", details.Select(detail => (int)detail.State).ToArray());
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string UpsertBuckets = """
        WITH input AS MATERIALIZED (
            SELECT * FROM unnest(@series, @buckets, @counts, @mins, @maxs, @lasts, @last_times,
                @integrals, @covered, @bytes, @totals, @free, @qualities, @running, @stopped, @other)
            AS i(series, bucket, count, lo, hi, last_value, last_time, integral, covered, bytes, total, free, quality, running, stopped, other)
        ), changed AS (
            INSERT INTO "MetricBuckets" AS b ("TenantId","SeriesId","Tier","Bucket","Count","Min","Max","Last","LastAt",
                "Integral","Covered","Bytes","TotalBytes","FreeBytes","Quality","Running","Stopped","Other")
            SELECT @tenant, series, 60, bucket, count, lo, hi, last_value, last_time, integral, covered, bytes,
                total, free, quality, running, stopped, other FROM input
            ON CONFLICT ("TenantId","SeriesId","Tier","Bucket") DO UPDATE SET
                "Count" = b."Count" + EXCLUDED."Count",
                "Min" = LEAST(b."Min", EXCLUDED."Min"),
                "Max" = GREATEST(b."Max", EXCLUDED."Max"),
                "Last" = CASE WHEN b."LastAt" IS NULL OR EXCLUDED."LastAt" >= b."LastAt" THEN EXCLUDED."Last" ELSE b."Last" END,
                "TotalBytes" = CASE WHEN b."LastAt" IS NULL OR EXCLUDED."LastAt" >= b."LastAt" THEN EXCLUDED."TotalBytes" ELSE b."TotalBytes" END,
                "FreeBytes" = CASE WHEN b."LastAt" IS NULL OR EXCLUDED."LastAt" >= b."LastAt" THEN EXCLUDED."FreeBytes" ELSE b."FreeBytes" END,
                "LastAt" = GREATEST(b."LastAt", EXCLUDED."LastAt"),
                "Integral" = b."Integral" + EXCLUDED."Integral",
                "Covered" = b."Covered" + EXCLUDED."Covered",
                "Bytes" = b."Bytes" + EXCLUDED."Bytes",
                "Quality" = b."Quality" | EXCLUDED."Quality",
                "Running" = b."Running" + EXCLUDED."Running",
                "Stopped" = b."Stopped" + EXCLUDED."Stopped",
                "Other" = b."Other" + EXCLUDED."Other"
        )
        SELECT count(*) FROM input i WHERE NOT EXISTS (
            SELECT 1 FROM "MetricBuckets" b WHERE b."TenantId" = @tenant
                AND b."SeriesId" = i.series AND b."Tier" = 60 AND b."Bucket" = i.bucket);
        """;
}

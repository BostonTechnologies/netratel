namespace NetRatel.Infrastructure.Metrics;

public sealed partial class MetricHistoryStore
{
    private const string QuerySql = """
        WITH retained AS (
            SELECT * FROM "MetricBuckets" WHERE "TenantId" = @tenant AND "SeriesId" = ANY(@ids)
                AND "Tier" = 60 AND "Bucket" >= @minute_from AND "Bucket" < @minute_to
            UNION ALL
            SELECT * FROM "MetricBuckets" WHERE "TenantId" = @tenant AND "SeriesId" = ANY(@ids)
                AND "Tier" = 300 AND "Bucket" >= @five_from AND "Bucket" < @five_to
            UNION ALL
            SELECT * FROM "MetricBuckets" WHERE "TenantId" = @tenant AND "SeriesId" = ANY(@ids)
                AND "Tier" = 3600 AND "Bucket" >= @hour_from AND "Bucket" < @hour_to
        ), clipped AS (
            SELECT retained.*,
                EXTRACT(epoch FROM greatest(@from - "Bucket", interval '0 seconds')) * 1000000 AS left_us,
                LEAST(EXTRACT(epoch FROM @to - "Bucket"), "Tier") * 1000000 AS right_us,
                "Tier"::numeric * 1000000 AS tier_us
            FROM retained
        ), portions AS (
            SELECT "SeriesId" AS series, date_bin(make_interval(secs => @step), "Bucket", TIMESTAMPTZ '1970-01-01 00:00:00+00') AS bin,
                "Count" AS count, "Min" AS lo, "Max" AS hi,
                CASE WHEN "LastAt" >= @from AND "LastAt" < @to THEN "Last" END AS last_value,
                CASE WHEN "LastAt" >= @from AND "LastAt" < @to THEN "LastAt" END AS last_time,
                "Integral" * ((right_us - left_us)::double precision / tier_us::double precision) AS integral,
                div("Covered" * right_us, tier_us) - div("Covered" * left_us, tier_us) AS covered,
                div("Bytes" * right_us, tier_us) - div("Bytes" * left_us, tier_us) AS bytes,
                CASE WHEN "LastAt" >= @from AND "LastAt" < @to THEN "TotalBytes" END AS total,
                CASE WHEN "LastAt" >= @from AND "LastAt" < @to THEN "FreeBytes" END AS free,
                "Quality" | CASE WHEN left_us > 0 OR right_us < tier_us THEN 768 ELSE 0 END AS quality,
                div("Running" * right_us, tier_us) - div("Running" * left_us, tier_us) AS running,
                div("Stopped" * right_us, tier_us) - div("Stopped" * left_us, tier_us) AS stopped,
                div("Other" * right_us, tier_us) - div("Other" * left_us, tier_us) AS other
            FROM clipped
        ), display AS (
            SELECT series, bin,
        """ + "\n" + AggregateHistoryColumns + "\n" + """
            FROM portions GROUP BY series, bin
        ), summary AS (
            SELECT series, NULL::timestamptz AS bin,
        """ + "\n" + AggregateHistoryColumns + "\n" + """
            FROM display GROUP BY series
        )
        SELECT * FROM display
        UNION ALL
        SELECT * FROM summary
        ORDER BY series, bin NULLS FIRST
        """;

    private const string AggregateHistoryColumns = """
        sum(count)::bigint AS count, min(lo) AS lo, max(hi) AS hi,
        (array_agg(last_value ORDER BY last_time DESC NULLS LAST))[1] AS last_value, max(last_time) AS last_time,
        sum(integral) AS integral, sum(covered)::bigint AS covered, sum(bytes) AS bytes,
        (array_agg(total ORDER BY last_time DESC NULLS LAST))[1] AS total,
        (array_agg(free ORDER BY last_time DESC NULLS LAST))[1] AS free, bit_or(quality) AS quality,
        sum(running)::bigint AS running, sum(stopped)::bigint AS stopped, sum(other)::bigint AS other
        """;

    private const string CatalogueSql = """
        SELECT s."Id", s."Kind", s."Resource", s."Label", retained.first_at, s."LastAt", s."Quality"
        FROM "MetricSeries" s
        CROSS JOIN LATERAL (
            SELECT min(candidate."Bucket") AS first_at FROM (
                (SELECT b."Bucket" FROM "MetricBuckets" b WHERE b."TenantId" = @tenant AND b."SeriesId" = s."Id"
                    AND b."Tier" = 60 AND b."Bucket" >= @minute_from AND b."Bucket" < @minute_to ORDER BY b."Bucket" LIMIT 1)
                UNION ALL
                (SELECT b."Bucket" FROM "MetricBuckets" b WHERE b."TenantId" = @tenant AND b."SeriesId" = s."Id"
                    AND b."Tier" = 300 AND b."Bucket" >= @five_from AND b."Bucket" < @five_to ORDER BY b."Bucket" LIMIT 1)
                UNION ALL
                (SELECT b."Bucket" FROM "MetricBuckets" b WHERE b."TenantId" = @tenant AND b."SeriesId" = s."Id"
                    AND b."Tier" = 3600 AND b."Bucket" >= @hour_from AND b."Bucket" < @hour_to ORDER BY b."Bucket" LIMIT 1)
            ) candidate
        ) retained
        WHERE s."TenantId" = @tenant AND s."AgentId" = @agent AND s."Id" > @after AND retained.first_at IS NOT NULL
        ORDER BY s."Id" LIMIT @limit
        """;

    private const string ServiceDetailsSql = """
        WITH checkpoints AS (
            SELECT "SeriesId", "ObservedAt", "State",
                lag("State") OVER w AS previous_state, lag("ObservedAt") OVER w AS previous_at
            FROM "MetricServiceDetails"
            WHERE "TenantId" = @tenant AND "SeriesId" = ANY(@ids) AND "ObservedAt" >= @from AND "ObservedAt" < @to
            WINDOW w AS (PARTITION BY "SeriesId" ORDER BY "ObservedAt")
        ), selected AS (
            SELECT "SeriesId", "ObservedAt", "State", row_number() OVER (PARTITION BY "SeriesId" ORDER BY "ObservedAt") AS ordinal
            FROM checkpoints WHERE "State" IS DISTINCT FROM previous_state OR "ObservedAt" - previous_at > interval '45 seconds'
                OR date_bin(interval '5 minutes', "ObservedAt", TIMESTAMPTZ '1970-01-01 00:00:00+00')
                    <> date_bin(interval '5 minutes', previous_at, TIMESTAMPTZ '1970-01-01 00:00:00+00')
        )
        SELECT "SeriesId", "ObservedAt", "State" FROM selected WHERE ordinal <= @limit
        UNION ALL
        SELECT predecessor."SeriesId", predecessor."ObservedAt", predecessor."State" FROM unnest(@ids) id
        CROSS JOIN LATERAL (
            SELECT "SeriesId", "ObservedAt", "State" FROM "MetricServiceDetails"
            WHERE "TenantId" = @tenant AND "SeriesId" = id AND "ObservedAt" >= @left AND "ObservedAt" < @from
            ORDER BY "ObservedAt" DESC LIMIT 1
        ) predecessor
        ORDER BY 1, 2
        """;
}

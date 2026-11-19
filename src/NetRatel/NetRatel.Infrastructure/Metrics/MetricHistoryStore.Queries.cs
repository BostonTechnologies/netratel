using System.Data;
using Microsoft.EntityFrameworkCore;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;
using Npgsql;
using static NetRatel.Infrastructure.Metrics.MetricMinuteAccumulator;

namespace NetRatel.Infrastructure.Metrics;

public sealed partial class MetricHistoryStore
{
    private static readonly SemaphoreSlim HistoryReads = new(8, 8);

    public async Task<MetricCatalogue> CatalogueAsync(int tenant, Guid agent, long after, int count, CancellationToken ct)
    {
        _db.Database.SetCommandTimeout(5);
        await using var snapshot = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        var policy = await ReadPolicyRowAsync(tenant, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var bounds = RetainedTiers.For(policy, now);
        await using var command = await CommandAsync(CatalogueSql, ct).ConfigureAwait(false);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("agent", agent);
        command.Parameters.AddWithValue("after", after);
        command.Parameters.AddWithValue("limit", count + 1);
        var series = new List<MetricSeriesDescription>(count + 1);
        BindRanges(command, bounds, bounds.Earliest, now + ClockLead);
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                series.Add(Describe(reader.GetInt64(0), agent, (MetricHistoryKind)reader.GetInt32(1), reader.GetString(2),
                    reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5),
                    (MetricHistoryQuality)reader.GetInt32(6), now));
        }
        var status = await StatusAsync(tenant, policy, ct).ConfigureAwait(false);
        var more = series.Count > count;
        if (more) series.RemoveAt(count);
        await snapshot.CommitAsync(ct).ConfigureAwait(false);
        return new(series, more ? FormattableString.Invariant($"{tenant}:{agent:N}:{series[^1].Id}") : null, status);
    }

    public async Task<IReadOnlyList<MetricSeriesDescription>> ScopeAsync(int tenant, Guid agent, long[] ids, CancellationToken ct)
    {
        return await _db.Set<MetricSeriesRow>().AsNoTracking()
            .Where(row => row.TenantId == tenant && row.AgentId == agent && ids.Contains(row.Id))
            .Select(row => new MetricSeriesDescription(row.Id, row.AgentId, row.Kind, row.Resource, row.Label,
                "", row.FirstAt, row.LastSourceAt ?? row.LastAt, row.Quality, false)).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<MetricHistoryResponse> QueryAsync(int tenant, MetricHistoryQuery query, CancellationToken ct)
    {
        if (!await HistoryReads.WaitAsync(0, ct).ConfigureAwait(false)) throw new MetricHistoryBusyException();
        try
        {
            _db.Database.SetCommandTimeout(8);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            return await ReadRangeAsync(tenant, query, linked.Token).ConfigureAwait(false);
        }
        finally { HistoryReads.Release(); }
    }

    private async Task<MetricHistoryResponse> ReadRangeAsync(int tenant, MetricHistoryQuery query, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var requestedFrom = query.FromUtc.ToUniversalTime();
        var to = query.ToExclusiveUtc.ToUniversalTime();
        await using var snapshot = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        var policy = await ReadPolicyRowAsync(tenant, ct).ConfigureAwait(false);
        var tiers = RetainedTiers.For(policy, now);
        var status = await StatusAsync(tenant, policy, ct).ConfigureAwait(false);
        var series = (await ScopeAsync(tenant, query.AgentId, query.SeriesIds, ct).ConfigureAwait(false))
            .ToDictionary(row => row.Id, row => Describe(row.Id, row.AgentId, row.Kind, row.ResourceKey,
                row.DisplayName, Max(row.FirstRetainedAtUtc, tiers.Earliest), row.LastObservedAtUtc, row.Quality, now));
        if (series.Count != query.SeriesIds.Length) throw new KeyNotFoundException("metrics_series_not_found");

        var from = Max(requestedFrom, tiers.Earliest);
        var step = CalculateStep(query, from, to, tiers);
        var detailFrom = now.AddDays(-(policy?.BaseDays ?? MetricHistoryLimits.BaseDays));
        var samples = series.ToDictionary(item => item.Key, _ => new List<MetricHistoryPoint>());
        var summaries = new Dictionary<long, MetricHistoryStatistics>();
        var details = new Dictionary<long, List<MetricServiceCheckpoint>>();
        var detailLimit = 0;

        if (from < to)
        {
            await ReadPointsAsync(tenant, query.SeriesIds, requestedFrom, from, to, tiers, step, series, samples, summaries, ct).ConfigureAwait(false);
            var serviceIds = series.Values.Where(item => item.Kind == MetricHistoryKind.ServiceState).Select(item => item.Id).ToArray();
            if (serviceIds.Length > 0 && to > detailFrom)
            {
                detailLimit = MetricHistoryLimits.MaximumServiceDetails / serviceIds.Length;
                details = await ReadDetailsAsync(tenant, serviceIds, Max(requestedFrom, detailFrom), to, detailFrom, detailLimit, ct).ConfigureAwait(false);
            }
        }

        var result = query.SeriesIds.Select(id =>
        {
            var serviceDetails = details.GetValueOrDefault(id);
            var limited = detailLimit > 0 && serviceDetails?.Count > detailLimit;
            if (limited) serviceDetails!.RemoveRange(detailLimit, serviceDetails.Count - detailLimit);
            var summary = summaries.GetValueOrDefault(id) ?? Empty(series[id].Quality);
            if (series[id].Kind == MetricHistoryKind.ServiceState && summary.ServiceDuration is null)
                summary = summary with { ServiceDuration = new(0, 0, 0, Microseconds(requestedFrom, to)) };
            return new MetricHistorySeries(series[id], summary, samples[id], serviceDetails ?? [], limited);
        }).ToArray();
        await snapshot.CommitAsync(ct).ConfigureAwait(false);
        var disposition = from >= to ? "outside_retention" : result.Any(item => item.Points.Count > 0) ? "available" :
            !status.Enabled ? "collection_disabled" : status.LastPersistedAtUtc is null ? "not_yet_collected" : "no_observations";
        return new(requestedFrom, to, step, tiers.Earliest, detailFrom, disposition, status, result);
    }

    private async Task ReadPointsAsync(int tenant, long[] ids, DateTimeOffset requestedFrom, DateTimeOffset from, DateTimeOffset to,
        RetainedTiers tiers, int step, Dictionary<long, MetricSeriesDescription> series, Dictionary<long, List<MetricHistoryPoint>> samples,
        Dictionary<long, MetricHistoryStatistics> summaries, CancellationToken ct)
    {
        await using var command = await CommandAsync(QuerySql, ct).ConfigureAwait(false);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("ids", ids);
        command.Parameters.AddWithValue("step", step);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        BindRanges(command, tiers, from, to);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var bin = reader.IsDBNull(1) ? null : (DateTimeOffset?)reader.GetFieldValue<DateTimeOffset>(1);
            var first = bin is { } start ? Max(from, start) : requestedFrom;
            var last = bin is { } point ? Min(to, point.AddSeconds(step)) : to;
            var statistics = Statistics(reader, series[id].Kind, Microseconds(first, last));
            if (bin is null) summaries.Add(id, statistics);
            else samples[id].Add(new(first, last, statistics));
        }
    }

    private async Task<Dictionary<long, List<MetricServiceCheckpoint>>> ReadDetailsAsync(int tenant, long[] series,
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset retainedFrom, int limit, CancellationToken ct)
    {
        await using var command = await CommandAsync(ServiceDetailsSql, ct).ConfigureAwait(false);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("ids", series);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("left", Max(retainedFrom, from.AddSeconds(-45)));
        command.Parameters.AddWithValue("limit", limit + 1);
        var result = new Dictionary<long, List<MetricServiceCheckpoint>>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            if (!result.TryGetValue(id, out var items)) result.Add(id, items = []);
            var at = reader.GetFieldValue<DateTimeOffset>(1);
            items.Add(new(at, (ClientServiceState)reader.GetInt32(2), at < from));
        }
        return result;
    }

    private static MetricHistoryStatistics Statistics(NpgsqlDataReader row, MetricHistoryKind kind, long extent)
    {
        var covered = row.GetInt64(8);
        var quality = (MetricHistoryQuality)row.GetInt32(12);
        if (covered < extent) quality |= MetricHistoryQuality.PartialCoverage;
        var network = kind is MetricHistoryKind.NetworkRx or MetricHistoryKind.NetworkTx;
        var bytes = network && covered > 0 ? (decimal?)row.GetDecimal(9) : null;
        MetricServiceDuration? service = null;
        if (kind == MetricHistoryKind.ServiceState)
        {
            var running = row.GetInt64(13);
            var stopped = row.GetInt64(14);
            var other = row.GetInt64(15);
            service = new(running, stopped, other, Math.Max(0, extent - running - stopped - other));
        }
        double? Number(int index) => row.IsDBNull(index) ? null : row.GetDouble(index);
        decimal? Decimal(int index) => row.IsDBNull(index) ? null : row.GetDecimal(index);
        double? weighted = covered == 0 || service is not null ? null : network
            ? (double)bytes!.Value * 1_000_000d / covered : row.GetDouble(7) / covered;
        var observedAt = row.IsDBNull(6) ? null : (DateTimeOffset?)row.GetFieldValue<DateTimeOffset>(6);
        var attributedAt = row.IsDBNull(16) ? null : (DateTimeOffset?)row.GetFieldValue<DateTimeOffset>(16);
        return new(row.GetInt64(2), Number(5), observedAt,
            Number(3), Number(4), weighted, covered, ExactBytes(bytes), ExactBytes(Decimal(10)), ExactBytes(Decimal(11)),
            quality, service, attributedAt == observedAt ? null : attributedAt);
    }

    private static MetricHistoryStatistics Empty(MetricHistoryQuality quality) =>
        new(0, null, null, null, null, null, 0, null, null, null, quality | MetricHistoryQuality.PartialCoverage, null);

    private static MetricSeriesDescription Describe(long id, Guid agent, MetricHistoryKind kind, string key, string label,
        DateTimeOffset first, DateTimeOffset last, MetricHistoryQuality quality, DateTimeOffset now) =>
        new(id, agent, kind, key, label, MetricHistoryLimits.Unit(kind), first, last, quality, last >= now.AddMinutes(-2));

    private static int CalculateStep(MetricHistoryQuery query, DateTimeOffset from, DateTimeOffset to, RetainedTiers tiers)
    {
        var coarsest = from < tiers.HourEnd ? 3600 : from < tiers.FiveEnd ? 300 : 60;
        var limit = Math.Min(query.MaximumPointsPerSeries ?? MetricHistoryLimits.DefaultPointsPerSeries,
            MetricHistoryLimits.MaximumTotalPoints / query.SeriesIds.Length);
        var seconds = Math.Max((to - from).TotalSeconds / (limit - 1), query.ResolutionSeconds ?? 60);
        return (int)Math.Ceiling(seconds / coarsest) * coarsest;
    }

    private static void BindRanges(NpgsqlCommand command, RetainedTiers tiers, DateTimeOffset from, DateTimeOffset to)
    {
        command.Parameters.AddWithValue("hour_from", Floor(from, 3600));
        command.Parameters.AddWithValue("hour_to", Min(tiers.HourEnd, to));
        command.Parameters.AddWithValue("five_from", Max(Floor(from, 300), tiers.HourEnd));
        command.Parameters.AddWithValue("five_to", Min(tiers.FiveEnd, to));
        command.Parameters.AddWithValue("minute_from", Max(Floor(from, 60), tiers.FiveEnd));
        command.Parameters.AddWithValue("minute_to", to);
    }

    private sealed record RetainedTiers(DateTimeOffset Earliest, DateTimeOffset HourEnd, DateTimeOffset FiveEnd)
    {
        public static RetainedTiers For(MetricPolicyRow? policy, DateTimeOffset now)
        {
            var earliest = now.AddDays(-(policy?.HourlyDays ?? MetricHistoryLimits.HourlyDays));
            var five = Ceil(Max(now.AddDays(-(policy?.BaseDays ?? MetricHistoryLimits.BaseDays)), policy?.BasePurgedBefore ?? default), 300);
            var hour = Ceil(Max(now.AddDays(-(policy?.FiveMinuteDays ?? MetricHistoryLimits.FiveMinuteDays)), policy?.FivePurgedBefore ?? default), 3600);
            five = Min(five, policy?.FiveSealedBefore ?? default);
            hour = Min(Min(hour, Floor(five, 3600)), policy?.HourSealedBefore ?? default);
            return new(earliest, hour, five);
        }

        private static DateTimeOffset Ceil(DateTimeOffset value, int seconds)
        {
            var floor = Floor(value, seconds);
            return floor == value ? floor : floor.AddSeconds(seconds);
        }
    }
}

using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Infrastructure.Metrics;

internal sealed class MetricMinuteAccumulator
{
    public static readonly TimeSpan Lateness = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ClockLead = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ClockReadTolerance = TimeSpan.FromMilliseconds(25);

    private readonly Dictionary<(MetricSeriesRow Series, DateTimeOffset Bucket), MetricBucketRow> _buckets = [];
    public IEnumerable<MetricBucketRow> Buckets => _buckets.Values;
    public List<(MetricSeriesRow Series, DateTimeOffset At, ClientServiceState State)> Details { get; } = [];
    public int Late { get; private set; }

    public bool Accept(MetricSeriesRow series, MetricReading reading, MetricIngress input, DateTimeOffset sealedBefore,
        DateTimeOffset? enabledSince)
    {
        // Match the PostgreSQL timestamp precision before ordering or deduplicating a reading.
        var orderAt = ToMicrosecond(reading.OrderAt);
        reading = reading with { At = ToMicrosecond(reading.At) };
        // The measurement is observed at the closing read. Its interval contribution
        // still ends before that read, even when the timestamps straddle bucket boundaries.
        var sampleMinute = Floor(orderAt, 60);
        if (sampleMinute < sealedBefore || orderAt > input.ReceivedAt + ClockLead || reading.At > input.ReceivedAt + ClockLead ||
            orderAt < enabledSince)
        {
            Late++;
            return false;
        }

        if (series.StreamOrdinal > input.Fence.RegistrationOrdinal ||
            (series.StreamId == input.Fence.EvidenceStreamId && series.Sequence >= input.Sequence) ||
            (reading.Collection is not null && reading.Collection == series.CollectionId) ||
            orderAt <= series.LastAt)
            return false;

        var quality = reading.Quality;
        if (reading.Window is { } window && reading.Value is not null)
        {
            var start = orderAt.AddTicks(-window.ElapsedMicroseconds * TimeSpan.TicksPerMicrosecond);
            if ((window.StartedAtUtc - start).Duration() > ClockReadTolerance || (reading.At - orderAt).Duration() > ClockReadTolerance)
                quality |= MetricHistoryQuality.EstimatedTiming;
            var coveredFrom = Max(Max(start, series.LastIntervalEnd ?? start), enabledSince.GetValueOrDefault());
            if (coveredFrom < sealedBefore) Late++;
            AddInterval(series, reading, start, Max(coveredFrom, sealedBefore), orderAt, quality, reading.Bytes);
            series.LastIntervalEnd = orderAt;
        }
        else if (series.StreamId == input.Fence.EvidenceStreamId && series.LastAt >= enabledSince.GetValueOrDefault() &&
            series.WatchRevision == reading.WatchRevision)
        {
            CarryGauge(series, reading, sealedBefore);
        }

        var bucket = Bucket(series, sampleMinute);
        bucket.Quality |= quality;
        if (reading.Value is { } value)
        {
            Observe(bucket, value);
            if (bucket.LastOrderAt is null || orderAt >= bucket.LastOrderAt)
            {
                bucket.Last = value;
                bucket.LastAt = reading.At;
                bucket.LastOrderAt = orderAt;
                bucket.TotalBytes = reading.DiskTotal;
                bucket.FreeBytes = reading.DiskFree;
            }
            bucket.Count++;
        }
        else if (reading.State is { } state)
        {
            bucket.Count++;
            bucket.LastAt = reading.At;
            bucket.LastOrderAt = orderAt;
            Details.Add((series, reading.At, state));
        }

        series.LastAt = orderAt;
        series.LastSourceAt = reading.At;
        series.Sequence = input.Sequence;
        series.StreamId = input.Fence.EvidenceStreamId;
        series.StreamOrdinal = input.Fence.RegistrationOrdinal;
        series.CollectionId = reading.Collection;
        series.WatchRevision = reading.WatchRevision;
        series.State = reading.State;
        series.Gauge = reading.Value;
        series.Quality = quality;
        series.Label = reading.Label;
        return true;
    }

    private void CarryGauge(MetricSeriesRow series, MetricReading reading, DateTimeOffset sealedBefore)
    {
        var freshness = reading.Kind switch
        {
            MetricHistoryKind.MemoryPercent or MetricHistoryKind.MemoryUsedBytes or MetricHistoryKind.MemoryTotalBytes => 10,
            MetricHistoryKind.DiskUsedPercent or MetricHistoryKind.ServiceState => 45,
            _ => 0
        };
        if (freshness == 0 || series.Gauge is null && !IsKnown(series.State)) return;
        var end = Min(reading.At, series.LastAt.AddSeconds(freshness));
        AddInterval(series, reading with { Value = series.Gauge, State = series.State }, series.LastAt,
            Max(series.LastAt, sealedBefore), end, series.Quality, null);
    }

    private void AddInterval(MetricSeriesRow series, MetricReading reading, DateTimeOffset sourceStart,
        DateTimeOffset from, DateTimeOffset end, MetricHistoryQuality quality, ulong? bytes)
    {
        var sourceDuration = Microseconds(sourceStart, end);
        if (sourceDuration <= 0 || from >= end) return;
        for (var cursor = from; cursor < end;)
        {
            var minute = Floor(cursor, 60);
            var to = Min(minute.AddMinutes(1), end);
            var duration = Microseconds(cursor, to);
            if (duration > 0)
            {
                var bucket = Bucket(series, minute);
                bucket.Covered += duration;
                bucket.Quality |= quality;
                if (bytes is { } delta)
                {
                    bucket.Bytes += Allocate(delta, Microseconds(sourceStart, cursor), Microseconds(sourceStart, to), sourceDuration);
                    if (duration != sourceDuration) bucket.Quality |= MetricHistoryQuality.EstimatedTiming;
                }
                else if (reading.Value is { } value) bucket.Integral += value * duration;
                if (reading.Window is not null && reading.Value is { } rate) Observe(bucket, rate);
                switch (reading.State)
                {
                    case ClientServiceState.Running: bucket.Running += duration; break;
                    case ClientServiceState.Stopped: bucket.Stopped += duration; break;
                    case { } state when IsKnown(state): bucket.Other += duration; break;
                }
            }
            cursor = to;
        }
    }

    private static void Observe(MetricBucketRow bucket, double value)
    {
        bucket.Min = bucket.Min is { } min ? Math.Min(min, value) : value;
        bucket.Max = bucket.Max is { } max ? Math.Max(max, value) : value;
    }

    private MetricBucketRow Bucket(MetricSeriesRow series, DateTimeOffset minute)
    {
        if (_buckets.TryGetValue((series, minute), out var bucket)) return bucket;
        bucket = new() { TenantId = series.TenantId, SeriesId = series.Id, Bucket = minute, Tier = 60 };
        _buckets.Add((series, minute), bucket);
        return bucket;
    }

    public void BindSeriesIds()
    {
        foreach (var (key, bucket) in _buckets) bucket.SeriesId = key.Series.Id;
    }

    internal static bool IsKnown(ClientServiceState? state) => state is not
        (null or ClientServiceState.Unknown or ClientServiceState.Unsupported);
    internal static DateTimeOffset Floor(DateTimeOffset date, int seconds)
    {
        var ticks = seconds * TimeSpan.TicksPerSecond;
        return new(date.UtcTicks / ticks * ticks, TimeSpan.Zero);
    }
    private static DateTimeOffset ToMicrosecond(DateTimeOffset date) =>
        new(date.UtcTicks - date.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);

    internal static long Microseconds(DateTimeOffset from, DateTimeOffset to) => (to.UtcTicks - from.UtcTicks) / TimeSpan.TicksPerMicrosecond;
    internal static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    internal static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    internal static ulong Allocate(ulong bytes, long first, long last, long duration) =>
        checked((ulong)(((UInt128)bytes * (ulong)last / (ulong)duration) - ((UInt128)bytes * (ulong)first / (ulong)duration)));
}

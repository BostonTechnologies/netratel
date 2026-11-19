using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using NetRatel.Infrastructure.Metrics;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class MetricHistoryPostgresTests(PostgreSqlPersistenceFixture fixture)
{
    [Fact]
    public async Task Network_weights_exact_bytes_and_stitches_rollups_after_a_restart_and_purge()
    {
        var dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(await fixture.CreateDatabaseAsync()).Options;
        var clock = new EvidenceClock(new(2026, 11, 1, 10, 1, 0, TimeSpan.Zero));
        var start = clock.GetUtcNow().AddMinutes(-1);
        var end = start.AddMinutes(1);
        var client = new ClientKey(7, Guid.NewGuid());
        var other = new ClientKey(7, Guid.NewGuid());
        var fence = Fence(client);
        var generation = Guid.NewGuid();
        var disk = new TelemetryDisk("/", 0, 0, 0, 0, (1UL << 54) + 7, (1UL << 53) + 3,
            Guid.NewGuid(), start.AddSeconds(1), TelemetryDiskCollectionQuality.Complete);
        var batch = Enumerable.Range(1, 20).Select(i =>
        {
            var width = i <= 10 ? 5 : 1;
            var at = start.AddSeconds(i <= 10 ? i * 5 : 50 + i - 10);
            var bytes = i <= 10 ? 5_000UL : 10_000UL;
            return Frame(fence, (ulong)i, at, width, bytes, generation, disk, i == 20 ? at.AddSeconds(-10) : null);
        }).ToArray();
        var batchId = Guid.NewGuid();

        await using (var db = new OrchestratorDbContext(dbOptions))
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new() { Id = client.TenantId, Name = "Synthetic tenant" });
            db.Agents.AddRange(new Agent { Id = client.AgentId, TenantId = client.TenantId, Name = "Example a", Status = AgentStatus.Active },
                new Agent { Id = other.AgentId, TenantId = other.TenantId, Name = "Example b", Status = AgentStatus.Active });
            await db.SaveChangesAsync();

            var store = new MetricHistoryStore(db, clock);
            var initial = await store.ReadPolicyAsync(client.TenantId, default);
            Assert.Equal((true, 14, 90, 365), (initial.Enabled, initial.BaseDays, initial.FiveMinuteDays, initial.HourlyDays));
            var saved = await store.SavePolicyAsync(client.TenantId, new(0, true, 1, 2, 3, [], "Synthetic retention"), "operator", default);
            Assert.Equal(1, saved!.Revision);
        }

        await WithStoreAsync(store => store.PersistAsync(client.TenantId, batchId, batch, default));
        await WithStoreAsync(store => store.PersistAsync(client.TenantId, batchId, batch, default));
        await WithStoreAsync(store => store.PersistAsync(client.TenantId, Guid.NewGuid(), batch, default));
        await WithStoreAsync(store => store.PersistAsync(client.TenantId, Guid.NewGuid(),
            [Frame(Fence(other), 1, end, 5, 100, Guid.NewGuid(), null)], default));

        long rx = 0, tx = 0, diskId = 0;
        await WithStoreAsync(async store =>
        {
            var catalogue = await store.CatalogueAsync(client.TenantId, client.AgentId, 0, 50, default);
            rx = catalogue.Series.Single(row => row.Kind == MetricHistoryKind.NetworkRx && row.DisplayName == "veth0").Id;
            tx = catalogue.Series.Single(row => row.Kind == MetricHistoryKind.NetworkTx && row.DisplayName == "veth0").Id;
            diskId = catalogue.Series.Single(row => row.Kind == MetricHistoryKind.DiskUsedPercent).Id;
            var legacy = catalogue.Series.Single(row => row.Kind == MetricHistoryKind.NetworkRx && row.DisplayName == "legacy");
            var legacyResult = await store.QueryAsync(client.TenantId, new(client.AgentId, [legacy.Id], start, end), default);
            Assert.Null(legacyResult.Series[0].Summary.TransferredBytes);
            Assert.Null(legacyResult.Series[0].Summary.WeightedAverage);
            Assert.Equal(0, legacyResult.Series[0].Summary.CoveredMicroseconds);
            Assert.True(legacyResult.Series[0].Summary.Quality.HasFlag(MetricHistoryQuality.Legacy));
            Assert.NotNull(catalogue.Status.LastPersistedAtUtc);

            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.QueryAsync(other.TenantId,
                new(other.AgentId, [rx], start, end), default));
        });

        await CheckTotalsAsync(60);
        await WithStoreAsync(async store =>
        {
            var first = (await store.QueryAsync(client.TenantId, new(client.AgentId, [rx], start, start.AddSeconds(29)), default)).Series[0].Summary;
            var second = (await store.QueryAsync(client.TenantId, new(client.AgentId, [rx], start.AddSeconds(29), end), default)).Series[0].Summary;
            Assert.Equal(150_000m, decimal.Parse(first.TransferredBytes!) + decimal.Parse(second.TransferredBytes!));
            Assert.True(first.Quality.HasFlag(MetricHistoryQuality.EstimatedTiming));
            var latest = (await store.QueryAsync(client.TenantId, new(client.AgentId, [rx], start, end.AddSeconds(1)), default)).Series[0].Summary;
            Assert.Equal(end.AddSeconds(-10), latest.LastObservedAtUtc);
            Assert.Equal(end, latest.LastAttributedAtUtc);
            var diskHistory = (await store.QueryAsync(client.TenantId, new(client.AgentId, [diskId], start, end), default)).Series[0].Summary;
            Assert.Equal(1, diskHistory.Count);
            Assert.Equal(disk.TotalBytes!.Value.ToString(), diskHistory.DiskTotalBytes);
            Assert.Equal(disk.FreeBytes!.Value.ToString(), diskHistory.DiskFreeBytes);
        });

        clock.Now = clock.Now.AddDays(1).AddHours(2);
        await using (var db = new OrchestratorDbContext(dbOptions))
        await using (var held = await db.Database.BeginTransactionAsync())
        {
            Assert.True(await new MetricHistoryStore(db, clock).TryLockAsync(client.TenantId, default));
            await WithStoreAsync(async store => Assert.False(await store.MaintainAsync(client.TenantId, default)));
            await held.RollbackAsync();
        }
        await WithStoreAsync(async store => Assert.True(await store.MaintainAsync(client.TenantId, default)));
        await using (var db = new OrchestratorDbContext(dbOptions))
            Assert.True(await db.Set<MetricBucketRow>().AnyAsync(row => row.Tier == 60 && row.SeriesId == rx));
        await WithStoreAsync(async store => Assert.True(await store.MaintainAsync(client.TenantId, default)));
        await using (var db = new OrchestratorDbContext(dbOptions))
            Assert.False(await db.Set<MetricBucketRow>().AnyAsync(row => row.Tier == 60 && row.SeriesId == rx));
        await CheckTotalsAsync(300);

        clock.Now = clock.Now.AddDays(1);
        await WithStoreAsync(async store => Assert.True(await store.MaintainAsync(client.TenantId, default)));
        await CheckTotalsAsync(3600);
        await WithStoreAsync(async store =>
        {
            var late = Frame(fence, 100, end.AddSeconds(-1), 1, 8000, generation, null);
            await store.PersistAsync(client.TenantId, Guid.NewGuid(), [late], default);
        });
        await CheckTotalsAsync(3600);
        await WithStoreAsync(async store =>
        {
            var status = await store.StatusAsync(client.TenantId, default);
            Assert.True(status.LateObservations > 0);
            Assert.Null(await store.SavePolicyAsync(client.TenantId, new(0, false, 1, 2, 3, [], "Conflicting write"), "operator", default));
        });

        async Task WithStoreAsync(Func<MetricHistoryStore, Task> use)
        {
            await using var db = new OrchestratorDbContext(dbOptions);
            await use(new MetricHistoryStore(db, clock));
        }

        Task CheckTotalsAsync(int resolution) => WithStoreAsync(async store =>
        {
            // Use full retained backing intervals at each tier. Clipped coarse edges carry an attribution flag.
            var queryEnd = start.AddSeconds(resolution);
            var result = await store.QueryAsync(client.TenantId, new(client.AgentId, [rx, tx], start, queryEnd, MaximumPointsPerSeries: 2), default);
            var incoming = result.Series[0].Summary;
            Assert.Equal(resolution, result.ResolutionSeconds);
            Assert.Equal("150000", incoming.TransferredBytes);
            Assert.Equal("300000", result.Series[1].Summary.TransferredBytes);
            Assert.Equal(60_000_000, incoming.CoveredMicroseconds);
            Assert.Equal(2500d, incoming.WeightedAverage);
            Assert.Equal(1000d, incoming.Minimum);
            Assert.Equal(10000d, incoming.Maximum);
            Assert.Equal(20, incoming.Count);
            if (resolution > 60)
            {
                Assert.Equal(end.AddSeconds(-10), incoming.LastObservedAtUtc);
                Assert.Equal(end, incoming.LastAttributedAtUtc);
            }
        });
    }

    [Fact]
    public void Counter_fragments_conserve_large_bytes_and_do_not_infer_a_lost_window()
    {
        var start = new DateTimeOffset(2026, 11, 1, 10, 0, 56, TimeSpan.Zero).AddTicks(3);
        var fence = Fence(new(7, Guid.NewGuid()));
        var accumulator = new MetricMinuteAccumulator();
        var series = new MetricSeriesRow { TenantId = 7, AgentId = fence.Client.AgentId, Kind = MetricHistoryKind.NetworkRx,
            Label = "eth0", Resource = "device" };
        const ulong amount = (1UL << 61) + 5;
        Add(start, start.AddSeconds(5), 1, amount);
        var replacement = fence with { EvidenceStreamId = Guid.NewGuid(), RegistrationOrdinal = 2 };
        var duplicate = new MetricIngress(replacement, 2, start.AddSeconds(5), null, [], 512);
        Assert.False(accumulator.Accept(series, Reading(start, start.AddSeconds(5), amount), duplicate, start.AddMinutes(-1), null));
        Add(start.AddSeconds(10), start.AddSeconds(15), 3, 0);

        var buckets = accumulator.Buckets.OrderBy(bucket => bucket.Bucket).ToArray();
        Assert.Equal(2, buckets.Length);
        Assert.Equal((decimal)amount, buckets.Sum(bucket => bucket.Bytes));
        Assert.Equal(new long[] { 4_000_000, 6_000_000 }, buckets.Select(bucket => bucket.Covered));
        Assert.True(buckets[0].Quality.HasFlag(MetricHistoryQuality.EstimatedTiming));

        void Add(DateTimeOffset from, DateTimeOffset to, ulong sequence, ulong bytes)
        {
            var input = new MetricIngress(fence, sequence, to, null, [], 512);
            Assert.True(accumulator.Accept(series, Reading(from, to, bytes), input, from.AddMinutes(-1), null));
        }
    }

    private static MetricReading Reading(DateTimeOffset from, DateTimeOffset to, ulong bytes) =>
        new(MetricHistoryKind.NetworkRx, "device", "eth0", to, MetricHistoryQuality.None, bytes / 5d,
            new(from, to, 5_000_000, to), bytes);

    private static MonitoringEvidenceFence Fence(ClientKey client) => new(client, Guid.NewGuid(), 1, Guid.NewGuid(), 1);

    private static MetricIngress Frame(MonitoringEvidenceFence fence, ulong sequence, DateTimeOffset end, int seconds, ulong bytes,
        Guid generation, TelemetryDisk? disk, DateTimeOffset? wallEnd = null)
    {
        var window = new MetricMeasurementWindow(end.AddSeconds(-seconds), wallEnd ?? end, seconds * 1_000_000L, end);
        var snapshot = new TelemetrySnapshot(fence.Client, fence.ConnectionEpoch, sequence, end, end,
            new(55, null, null) { Window = window }, new(4096, 2048, 2048, 50), disk is null ? [] : [disk],
            [new("veth0", bytes / (double)seconds, bytes * 2d / seconds)
            {
                Evidence = new(new string('A', 64), "veth0", generation, MetricMeasurementQuality.Valid, bytes, bytes * 2, window)
            }, new("legacy", 100, 200)], null, IsAuthoritative: true)
        { CpuQuality = MetricMeasurementQuality.Valid, NetworkQuality = MetricMeasurementQuality.Valid };
        return new(fence, sequence, end, snapshot, null, 1024);
    }

    private sealed class EvidenceClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

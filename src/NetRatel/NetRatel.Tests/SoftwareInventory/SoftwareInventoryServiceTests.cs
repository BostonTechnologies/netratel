using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.SoftwareInventory;
using Xunit;

namespace NetRatel.Tests.SoftwareInventory;

public sealed class SoftwareInventoryServiceTests
{
    [Fact]
    public async Task Enrollment_is_idempotent_weekly_and_cache_reads_never_schedule_work()
    {
        await using var fixture = new Fixture();
        var service = fixture.Service;
        (await service.StatusAsync(7, fixture.Agent, default)).LatestScan.Should().BeNull();
        var work = await service.WorkAsync(7, fixture.Agent, default);
        work.Policy.Should().Be(new SoftwareInventoryPolicy());
        (await service.WorkAsync(7, fixture.Agent, default)).CollectionId.Should().Be(work.CollectionId);
        await fixture.Upload(work.CollectionId!.Value, [Package("tool", "1")]);
        var status = await service.StatusAsync(7, fixture.Agent, default);
        status.NextDueUtc.Should().BeOnOrAfter(fixture.Clock.Now.AddDays(7)).And.BeOnOrBefore(fixture.Clock.Now.AddDays(7.7));
        (await service.WorkAsync(7, fixture.Agent, default)).CollectionId.Should().BeNull();
        (await service.RefreshAsync(7, fixture.Agent, false, default)).LatestScan!.CollectionId.Should().Be(work.CollectionId.Value);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(30);
        var refresh = await service.RefreshAsync(7, fixture.Agent, false, default);
        refresh.LatestScan!.CollectionId.Should().NotBe(work.CollectionId.Value);
        (await service.RefreshAsync(7, fixture.Agent, true, default)).LatestScan!.CollectionId.Should().Be(refresh.LatestScan.CollectionId);
    }

    [Fact]
    public async Task Partial_scan_and_replayed_old_uploads_cannot_remove_or_replace_complete_inventory()
    {
        await using var fixture = new Fixture();
        var first = (await fixture.Service.WorkAsync(7, fixture.Agent, default)).CollectionId!.Value;
        var firstChunk = await fixture.Upload(first, [Package("tool", "1"), Package("keep", "1")]);
        var partial = (await fixture.Service.RefreshAsync(7, fixture.Agent, true, default)).LatestScan!.CollectionId;
        await fixture.Upload(partial, [Package("tool", "2")], false);
        var retained = await fixture.Service.InstallationsAsync(7, new(), default);
        retained.Items.Select(item => item.Package.Version).Should().OnlyContain(version => version == "1");
        retained.Total.Should().Be(2);
        (await fixture.Service.StatusAsync(7, fixture.Agent, default)).LatestScan!.Status.Should().Be("Partial");
        var next = (await fixture.Service.RefreshAsync(7, fixture.Agent, true, default)).LatestScan!.CollectionId;
        await fixture.Upload(next, [Package("tool", "2"), Package("added", "1")]);
        (await fixture.Service.UploadAsync(7, fixture.Agent, firstChunk, default)).Accepted.Should().BeTrue();
        var current = await fixture.Service.InstallationsAsync(7, new(), default);
        current.Items.Select(item => item.Package.Name).Should().BeEquivalentTo("tool", "added");
        current.Items.Single(item => item.Package.Name == "tool").Package.Version.Should().Be("2");
        var history = await fixture.Service.ChangesAsync(7, fixture.Agent, 0, 100, default);
        history.Items.Where(change => change.CollectionId == next).Select(change => change.Kind).Should().BeEquivalentTo("Added", "Removed", "Upgraded");
    }

    [Fact]
    public async Task Chunked_large_inventory_is_atomic_deduplicated_and_tenant_scoped()
    {
        await using var fixture = new Fixture();
        var id = (await fixture.Service.WorkAsync(7, fixture.Agent, default)).CollectionId!.Value;
        var packages = Enumerable.Range(0, 1205).Select(index => Package("package-" + index, "1")).ToList();
        packages.Add(packages[0]);
        var chunks = SoftwareInventoryWire.Encode(id, new(packages, true, "linux", []), fixture.Clock.Now);
        foreach (var chunk in chunks.Reverse().SkipLast(1))
        {
            (await fixture.Service.UploadAsync(7, fixture.Agent, chunk, default)).Accepted.Should().BeTrue();
            (await fixture.Service.UploadAsync(7, fixture.Agent, chunk, default)).Accepted.Should().BeTrue();
        }
        (await fixture.Service.InstallationsAsync(7, new(), default)).Total.Should().Be(0);
        await fixture.Service.UploadAsync(7, fixture.Agent, chunks[0], default);
        (await fixture.Service.InstallationsAsync(7, new(Page: 12, PageSize: 100), default)).Items.Should().HaveCount(5);
        (await fixture.Service.InstallationsAsync(8, new(), default)).Total.Should().Be(0);
        (await fixture.Service.ScanAsync(8, fixture.Agent, id, default)).Should().BeNull();
        (await fixture.Service.AggregateAsync(7, new(Search: "package-1204"), default)).Items.Single().Devices.Should().Be(1);
        (await fixture.Service.UploadAsync(7, fixture.Agent, chunks[0] with { Complete = false }, default)).Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task Policy_disable_blocks_scheduled_scans_but_explicit_refresh_is_durable_offline()
    {
        await using var fixture = new Fixture();
        await fixture.Service.SetPolicyAsync(7, new(false, 2, "months"), default);
        (await fixture.Service.WorkAsync(7, fixture.Agent, default)).CollectionId.Should().BeNull();
        var refresh = await fixture.Service.RefreshAsync(7, fixture.Agent, true, default);
        refresh.LatestScan!.Status.Should().Be("Pending");
        (await fixture.Service.WorkAsync(7, fixture.Agent, default)).CollectionId.Should().Be(refresh.LatestScan.CollectionId);
        (await fixture.Service.PolicyAsync(8, default)).Should().Be(new SoftwareInventoryPolicy());
    }

    [Theory]
    [InlineData(0, "hours")]
    [InlineData(13, "months")]
    [InlineData(1, "minutes")]
    public void Cadence_is_bounded(int interval, string unit) => Assert.Throws<ArgumentException>(() => new SoftwareInventoryPolicy(true, interval, unit).Validate());

    [Fact]
    public void Calendar_months_and_jitter_are_deterministic()
    {
        var start = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);
        var policy = new SoftwareInventoryPolicy(true, 1, "months");
        var id = Guid.NewGuid();
        policy.NextDue(start, id).Should().Be(policy.NextDue(start, id));
        policy.NextDue(start, id).Should().BeOnOrAfter(start.AddMonths(1)).And.BeBefore(start.AddMonths(1).AddDays(3));
    }

    private static SoftwarePackage Package(string name, string version) => new(name, version, "Example", "x64", name, "dpkg");
    public sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public OrchestratorDbContext Db { get; } = new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Agent { get; } = Guid.NewGuid();
        public Clock Clock { get; } = new();
        public SoftwareInventoryService Service => new(Db, Clock);
        public Fixture()
        {
            Db.AddRange(new Tenant { Id = 7, Name = "Example" }, new Tenant { Id = 8, Name = "Other" }, new Agent { Id = Agent, TenantId = 7, Name = "device" });
            Db.SaveChanges();
        }
        public async Task<SoftwareChunkUpload> Upload(Guid id, IReadOnlyList<SoftwarePackage> packages, bool complete = true)
        {
            var chunk = SoftwareInventoryWire.Encode(id, new(packages, complete, "linux", complete ? [] : ["rpm_scan_failed"]), Clock.Now).Single();
            (await Service.UploadAsync(7, Agent, chunk, default)).Accepted.Should().BeTrue();
            return chunk;
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}

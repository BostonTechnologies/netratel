using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.SoftwareInventory;
using Xunit;

namespace NetRatel.Tests.SoftwareInventory;

public sealed class SoftwareInventoryRecoveryTests
{
    [Fact]
    public async Task Expired_collection_is_visible_without_read_side_effects_and_retry_preserves_completed_snapshot()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var agent = Guid.NewGuid();
        db.AddRange(new Tenant { Id = 7, Name = "Example" }, new Agent { Id = agent, TenantId = 7, Name = "example-host" });
        await db.SaveChangesAsync();
        var clock = new SoftwareInventoryServiceTests.Clock();
        var service = new SoftwareInventoryService(db, clock);
        var initial = (await service.WorkAsync(7, agent, default)).CollectionId!.Value;
        var packages = new SoftwarePackage[] { new("Retained Tool", "1", "Example", "x64", "retained", "dpkg") };
        var initialChunk = SoftwareInventoryWire.Encode(initial, new(packages, true, "linux", []), clock.Now).Single();
        (await service.UploadAsync(7, agent, initialChunk, default)).Status.Should().Be("Complete");
        var refresh = (await service.RefreshAsync(7, agent, true, default)).LatestScan!.CollectionId;
        await service.WorkAsync(7, agent, default);
        var replacement = SoftwareInventoryWire.Encode(refresh, new(packages, true, "linux", []), clock.Now).Single();
        clock.Now = clock.Now.AddMinutes(6);
        var status = await service.StatusAsync(7, agent, default);
        status.LatestScan!.Status.Should().Be("Error");
        status.LatestScan.Errors.Should().Contain("scan_expired");
        // A read reports the effective deadline but neither schedules work nor mutates the durable scan.
        (await db.Set<SoftwareInventoryScanRecord>().SingleAsync(row => row.CollectionId == refresh)).Status.Should().Be("Collecting");
        (await db.Set<SoftwareInventoryScanRecord>().CountAsync()).Should().Be(2);
        (await service.InstallationsAsync(7, new(), default)).Items.Should().ContainSingle().Which.Package.Name.Should().Be("Retained Tool");
        var retry = await service.RefreshAsync(7, agent, true, default);
        retry.LatestScan!.CollectionId.Should().NotBe(refresh);
        retry.LatestScan.Status.Should().Be("Pending");
        (await service.UploadAsync(7, agent, replacement, default)).Accepted.Should().BeFalse();
        (await service.InstallationsAsync(7, new(), default)).Total.Should().Be(1);
        (await service.SummaryAsync(7, default)).Inventoried.Should().Be(1);
    }
}

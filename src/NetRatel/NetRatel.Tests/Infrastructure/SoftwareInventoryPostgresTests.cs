using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.SoftwareInventory;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class SoftwareInventoryPostgresTests(PostgreSqlPersistenceFixture fixture)
{
    [Fact]
    public async Task Migration_concurrent_refresh_and_estate_queries_use_durable_tenant_scoped_authority()
    {
        var connection = await fixture.CreateDatabaseAsync();
        OrchestratorDbContext Open() => new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var other = Guid.NewGuid(); var unscanned = Guid.NewGuid();
        await using (var db = Open())
        {
            await db.Database.MigrateAsync();
            db.Database.HasPendingModelChanges().Should().BeFalse();
            db.AddRange(new Tenant { Id = 7, Name = "Example" }, new Tenant { Id = 8, Name = "Other" },
                new Agent { Id = first, TenantId = 7, Name = first.ToString(), DeviceInfoJson = """{"hostName":"linux-example","operatingSystem":"Linux"}""" },
                new Agent { Id = second, TenantId = 7, Name = "two", FriendlyName = "Office desktop" },
                new Agent { Id = unscanned, TenantId = 7, Name = "New device" },
                new Agent { Id = other, TenantId = 8, Name = "other" });
            await db.SaveChangesAsync();
        }
        async Task<SoftwareInventoryStatus> Refresh()
        {
            await using var db = Open();
            return await new SoftwareInventoryService(db, TimeProvider.System).RefreshAsync(7, first, true, default);
        }
        var refreshes = await Task.WhenAll(Refresh(), Refresh());
        refreshes.Select(status => status.LatestScan!.CollectionId).Distinct().Should().ContainSingle();
        foreach (var (tenant, agent) in new[] { (7, first), (7, second), (8, other) })
        {
            await using var db = Open();
            var service = new SoftwareInventoryService(db, TimeProvider.System);
            var work = await service.WorkAsync(tenant, agent, default);
            var packages = new List<SoftwarePackage> { new("Example Tool", "2", "Publisher", "x64", "example", "rpm") };
            if (agent == first) packages.Add(packages[0] with { Scope = "user" });
            foreach (var chunk in SoftwareInventoryWire.Encode(work.CollectionId!.Value, new(packages, true, "linux", []), DateTimeOffset.UtcNow))
                (await service.UploadAsync(tenant, agent, chunk, default)).Accepted.Should().BeTrue();
        }
        await using (var db = Open())
        {
            var service = new SoftwareInventoryService(db, TimeProvider.System);
            var installations = await service.InstallationsAsync(7, new(Publisher: "publish", Os: "linux", Source: "rpm", Status: "fresh", Sort: "client"), default);
            installations.Total.Should().Be(3);
            installations.Devices.Should().Be(2);
            installations.Items.Should().NotContain(item => item.AgentId == other);
            installations.Items.Where(item => item.AgentId == first).Should().OnlyContain(item => item.Client == "linux-example");
            installations.Items.Single(item => item.AgentId == second).Client.Should().Be("Office desktop");
            (await service.InstallationsAsync(7, new(Search: "linux-example"), default)).Total.Should().Be(2);
            var prevalence = await service.AggregateAsync(7, new(Search: "tool"), default);
            prevalence.Items.Should().ContainSingle().Which.Should().Be(new SoftwarePrevalenceDto("Example Tool", "2", "Publisher", "rpm", 2, 3));
            var exported = new List<SoftwareInstallationDto>();
            await foreach (var item in service.ExportAsync(8, new(), default)) exported.Add(item);
            exported.Should().ContainSingle().Which.AgentId.Should().Be(other);
            var summary = await service.SummaryAsync(7, default);
            summary.Clients.Should().Be(3);
            summary.Inventoried.Should().Be(2);
            summary.NeverScanned.Should().Be(1);
            summary.Installations.Should().Be(3);
            summary.Products.Should().Be(1);
            var clients = await service.ClientsAsync(7, new(), default);
            clients.Total.Should().Be(3);
            clients.Items.Single(item => item.AgentId == unscanned).Status.Should().Be("NeverScanned");
            clients.Items.Single(item => item.AgentId == first).InstalledEntries.Should().Be(2);
            clients.Items.Should().NotContain(item => item.AgentId == other);
            (await service.ClientsAsync(7, new(Search: "linux-example"), default)).Items.Should().ContainSingle().Which.Client.Should().Be("linux-example");
            (await service.ChangesAsync(7, first, 0, 50, default)).Items.Should().OnlyContain(item => item.Client == "linux-example");
            (await service.SummaryAsync(999, default)).Clients.Should().Be(0);
        }
    }

    [Fact]
    public async Task Multiple_chunks_with_submicrosecond_wire_timestamp_complete_across_independent_requests()
    {
        var connection = await fixture.CreateDatabaseAsync();
        OrchestratorDbContext Open() => new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);
        var agent = Guid.NewGuid();
        Guid collection;
        await using (var db = Open())
        {
            await db.Database.MigrateAsync();
            db.AddRange(new Tenant { Id = 7, Name = "Example" }, new Agent { Id = agent, TenantId = 7, Name = "inventory-example" });
            await db.SaveChangesAsync();
            collection = (await new SoftwareInventoryService(db, TimeProvider.System).WorkAsync(7, agent, default)).CollectionId!.Value;
        }
        // The old client outbox retains all seven fractional digits. Do not pre-normalize its wire payload.
        var now = DateTimeOffset.UtcNow;
        var detected = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10 + 7, TimeSpan.Zero);
        var packages = Enumerable.Range(0, 205).Select(index => new SoftwarePackage("Package " + index, "1", "Example", "x64", "pkg-" + index, "dpkg")).ToArray();
        var chunks = SoftwareInventoryWire.Encode(collection, new(packages, true, "linux", []), detected);
        chunks.Should().HaveCount(3);
        async Task<SoftwareChunkAcknowledgement> Upload(SoftwareChunkUpload chunk)
        {
            // HTTP ingestion creates a new scoped DbContext for every request, including duplicate retries.
            await using var db = Open();
            return await new SoftwareInventoryService(db, TimeProvider.System).UploadAsync(7, agent, chunk, default);
        }
        (await Upload(chunks[0])).Accepted.Should().BeTrue();
        (await Upload(chunks[0])).Accepted.Should().BeTrue();
        await using (var db = Open())
        {
            var service = new SoftwareInventoryService(db, TimeProvider.System);
            var status = await service.StatusAsync(7, agent, default);
            status.LatestScan!.ReceivedChunks.Should().Be(1);
            status.LastSuccessfulScanUtc.Should().BeNull();
            (await service.InstallationsAsync(7, new(), default)).Total.Should().Be(0);
            var stored = await db.Set<SoftwareInventoryScanRecord>().SingleAsync(row => row.CollectionId == collection);
            stored.DetectedAtUtc.Should().Be(detected.AddTicks(-7));
        }
        // Precision normalization must not accept a genuinely different collection timestamp.
        await Assert.ThrowsAsync<ArgumentException>(() => Upload(chunks[1] with { DetectedAtUtc = detected.AddMilliseconds(1) }));
        (await Upload(chunks[1])).Accepted.Should().BeTrue();
        var final = await Upload(chunks[2]);
        final.Accepted.Should().BeTrue();
        final.Status.Should().Be("Complete");
        (await Upload(chunks[0])).Status.Should().Be("Complete");
        await using (var db = Open())
        {
            var service = new SoftwareInventoryService(db, TimeProvider.System);
            var status = await service.StatusAsync(7, agent, default);
            status.LatestScan!.ReceivedChunks.Should().Be(3);
            status.LastSuccessfulScanUtc.Should().NotBeNull();
            (await service.InstallationsAsync(7, new(PageSize: 100), default)).Total.Should().Be(205);
            (await service.InstallationsAsync(8, new(), default)).Total.Should().Be(0);
            (await service.UploadAsync(8, agent, chunks[0], default)).Accepted.Should().BeFalse();
        }
    }
}

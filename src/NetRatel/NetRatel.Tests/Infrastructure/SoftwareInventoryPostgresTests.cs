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
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var other = Guid.NewGuid();
        await using (var db = Open())
        {
            await db.Database.MigrateAsync();
            db.Database.HasPendingModelChanges().Should().BeFalse();
            db.AddRange(new Tenant { Id = 7, Name = "Example" }, new Tenant { Id = 8, Name = "Other" },
                new Agent { Id = first, TenantId = 7, Name = "one" }, new Agent { Id = second, TenantId = 7, Name = "two" }, new Agent { Id = other, TenantId = 8, Name = "other" });
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
            var prevalence = await service.AggregateAsync(7, new(Search: "tool"), default);
            prevalence.Items.Should().ContainSingle().Which.Should().Be(new SoftwarePrevalenceDto("Example Tool", "2", "Publisher", "rpm", 2, 3));
            var exported = new List<SoftwareInstallationDto>();
            await foreach (var item in service.ExportAsync(8, new(), default)) exported.Add(item);
            exported.Should().ContainSingle().Which.AgentId.Should().Be(other);
        }
    }
}

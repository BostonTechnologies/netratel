using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class TenantServiceTests
{
    [Fact]
    public async Task UpdateAsync_Persists_AutoUpdate()
    {
        await using var db = CreateDb();
        var service = new TenantService(db);
        var created = await service.CreateAsync(new CreateTenantCommand(
            "Camelot-Estate",
            "Internet Lab",
            "Estate Hek",
            ["camelot-estate.co.za"],
            "Konrad",
            "security@camelot-estate.co.za",
            AutoUpdate: false));

        var updated = await service.UpdateAsync(new UpdateTenantCommand(
            created.TenantId,
            created.Name,
            created.Description,
            created.Location,
            created.Domains,
            created.ContactPerson,
            created.ContactEmail,
            AutoUpdate: true));

        updated.Should().NotBeNull();
        updated!.AutoUpdate.Should().BeTrue();
        updated.Version.Should().Be(2);
        var reloaded = await service.GetAsync(created.TenantId);
        reloaded!.AutoUpdate.Should().BeTrue();
        reloaded.Version.Should().Be(2);
    }

    [Fact]
    public async Task Versioned_mutations_reject_stale_tenant_revisions()
    {
        await using var db = CreateDb();
        var service = new TenantService(db);
        var created = await service.CreateAsync(new CreateTenantCommand(
            "Camelot-Estate",
            "Internet Lab",
            "Estate Hek",
            ["camelot-estate.co.za"],
            "Konrad",
            "security@camelot-estate.co.za",
            AutoUpdate: false));

        await FluentActions.Invoking(() => service.UpdateAsync(new UpdateTenantCommand(
            created.TenantId, created.Name, created.Description, created.Location, created.Domains,
            created.ContactPerson, created.ContactEmail, AutoUpdate: true, ExpectedVersion: 99)))
            .Should().ThrowAsync<TenantConcurrencyException>();
        await FluentActions.Invoking(() => service.DeleteAsync(created.TenantId, expectedVersion: 99))
            .Should().ThrowAsync<TenantConcurrencyException>();
    }

    private static OrchestratorDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new OrchestratorDbContext(options);
    }
}

using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class SecretProtectionPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Upgrade_protects_legacy_and_new_values_and_enforces_the_persisted_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseAsync(ct)).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261119161541_AddHistoricalMetrics", ct);

        const string original = "synthetic legacy value";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Secrets" ("TenantId", "Value", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES (7, {original}, now(), now())
            """, ct);
        await db.Database.MigrateAsync(ct);
        db.Database.HasPendingModelChanges().Should().BeFalse();
        var service = new SecretService(db, new EphemeralDataProtectionProvider());

        await service.ProtectLegacyValuesAsync(ct);
        var legacy = await db.Secrets.AsNoTracking().SingleAsync(ct);
        legacy.ValueProtected.Should().BeTrue();
        legacy.Value.Should().NotContain(original);
        (await service.GetAsync(legacy.Id, [7], ct))!.Value.Should().Be(original);
        (await service.GetAsync(legacy.Id, [8], ct)).Should().BeNull();
        (await service.UpdateAsync(new(legacy.Id, "different value", null), [8], ct)).Should().BeNull();
        (await service.DeleteAsync(legacy.Id, [8], ct)).Should().BeNull();

        const string value = "synthetic new value";
        var created = await service.CreateAsync(new(8, null, value, null), ct);
        var stored = await db.Secrets.AsNoTracking().SingleAsync(secret => secret.Id == created.Id, ct);
        stored.ValueProtected.Should().BeTrue();
        stored.Value.Should().NotContain(value);
        (await service.ListAsync([8], ct)).Should().ContainSingle().Which.Value.Should().Be(value);
        (await service.DeleteAsync(created.Id, [8], ct)).Should().NotBeNull();
    }
}

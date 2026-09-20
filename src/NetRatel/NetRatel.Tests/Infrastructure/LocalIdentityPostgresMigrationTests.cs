using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class LocalIdentityPostgresMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Fact]
    public async Task Identity_schema_is_applied_as_a_versioned_postgres_migration()
    {
        var options = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        await using var db = new NetRatelIdentityDbContext(options);

        await db.Database.MigrateAsync();

        (await db.Database.GetAppliedMigrationsAsync())
            .Should().Contain(migration => migration.EndsWith("AddLocalIdentity", StringComparison.Ordinal));
        (await db.Database.GetAppliedMigrationsAsync())
            .Should().Contain(migration => migration.EndsWith("AddScopedAuthorization", StringComparison.Ordinal));
        (await db.Database.GetAppliedMigrationsAsync())
            .Should().Contain(migration => migration.EndsWith("AddIntegrationCredentials", StringComparison.Ordinal));
        (await db.Database.GetAppliedMigrationsAsync())
            .Should().Contain(migration => migration.EndsWith("AddDeploymentBranding", StringComparison.Ordinal));
        var localUser = new LocalUser
        {
            Id = "local-admin",
            UserName = "admin@example.test",
            NormalizedUserName = "ADMIN@EXAMPLE.TEST",
            Email = "admin@example.test",
            NormalizedEmail = "ADMIN@EXAMPLE.TEST",
            PrincipalId = "principal-local-admin",
            DisplayName = "Local Administrator"
        };
        db.Users.Add(localUser);
        db.ApplicationPrincipals.Add(new ApplicationPrincipal { Id = localUser.PrincipalId, LocalUserId = localUser.Id });
        await db.SaveChangesAsync();

        (await db.Users.SingleAsync()).PrincipalId.Should().Be(localUser.PrincipalId);

        var access = new EffectiveAccessService(db, new ConfigurationBuilder().AddInMemoryCollection().Build());
        await access.ReconcileBuiltInRolesAsync();
        (await db.AccessRoles.CountAsync(role => role.IsBuiltIn)).Should().BeGreaterThan(0);

        var credentials = new IntegrationCredentialService(db);
        var created = await credentials.CreateAsync(localUser.PrincipalId, new(
            "Postgres verification",
            IntegrationCredentialPurpose.Api,
            DateTimeOffset.UtcNow.AddDays(7),
            [new(1, NetRatelPermissions.TelemetryRead)]));
        (await credentials.VerifyAsync(created.Secret, IntegrationCredentialPurpose.Api)).Should().NotBeNull();
    }
}

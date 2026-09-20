using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity;
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
    }
}

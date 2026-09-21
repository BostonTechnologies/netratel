using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Trait("category", "integration")]
public sealed class InstanceAdministratorInvariantPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Fact]
    public async Task Concurrent_destructive_mutations_leave_one_viable_administrator()
    {
        var options = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        await SeedAsync(options);

        await using var firstDb = new NetRatelIdentityDbContext(options);
        await using var secondDb = new NetRatelIdentityDbContext(options);
        var firstInvariant = new InstanceAdministratorInvariant(firstDb);
        var secondInvariant = new InstanceAdministratorInvariant(secondDb);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = firstInvariant.ExecuteDestructiveMutationAsync(async ct =>
        {
            (await firstInvariant.ViableAdministratorCountAsync(ct)).Should().Be(2);
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(ct);
            var user = await firstDb.Users.SingleAsync(candidate => candidate.Id == "first", ct);
            user.IsEnabled = false;
            await firstDb.SaveChangesAsync(ct);
            return true;
        }, TestContext.Current.CancellationToken);

        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = secondInvariant.ExecuteDestructiveMutationAsync(async ct =>
        {
            var viable = await secondInvariant.ViableAdministratorCountAsync(ct);
            if (viable <= 1)
                return false;

            var user = await secondDb.Users.SingleAsync(candidate => candidate.Id == "second", ct);
            user.IsEnabled = false;
            await secondDb.SaveChangesAsync(ct);
            return true;
        }, TestContext.Current.CancellationToken);

        releaseFirst.SetResult();
        (await first).Should().BeTrue();
        (await second).Should().BeFalse();

        await using var verify = new NetRatelIdentityDbContext(options);
        (await new InstanceAdministratorInvariant(verify).ViableAdministratorCountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    private static async Task SeedAsync(DbContextOptions<NetRatelIdentityDbContext> options)
    {
        await using var db = new NetRatelIdentityDbContext(options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AddRange(
            new ApplicationPrincipal { Id = "first-principal", LocalUserId = "first" },
            new ApplicationPrincipal { Id = "second-principal", LocalUserId = "second" },
            new LocalUser { Id = "first", UserName = "first@example.test", PrincipalId = "first-principal", IsEnabled = true, IsInstanceAdministrator = true },
            new LocalUser { Id = "second", UserName = "second@example.test", PrincipalId = "second-principal", IsEnabled = true, IsInstanceAdministrator = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}

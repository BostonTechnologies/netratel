using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.ComputerUse;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class ComputerUseSessionPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Concurrent_api_replicas_admit_one_controller_and_one_action()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connectionString).Options;
        await using (var migrate = new OrchestratorDbContext(options)) await migrate.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var request = new ComputerUseSessionCreate(id, id, 42, Guid.NewGuid(), Guid.NewGuid(), 2,
            new("operator@example.test", "fixture-client", "fixture-mcp", "https://mcp.example.test/mcp", "dev"),
            new(3, "fixture-user-hash", 100, "window-100"), "cua_driver", true, 300, 60, new string('A', 64), null, "fixture", now);
        await using var firstDb = new OrchestratorDbContext(options);
        await using var secondDb = new OrchestratorDbContext(options);
        var firstStore = new ComputerUseSessionStore(firstDb);
        var secondStore = new ComputerUseSessionStore(secondDb);
        var competing = request with { SessionId = Guid.NewGuid(), OperationId = Guid.NewGuid() };
        var admissions = await Task.WhenAll(firstStore.CreateAsync(request, default), secondStore.CreateAsync(competing, default));
        admissions.Count(result => result.Dispatch).Should().Be(1);
        admissions.Count(result => result.FailureCode == "computer_use_controller_conflict").Should().Be(1);
        var winner = admissions.Single(result => result.Dispatch).Session!;
        await using var settleDb = new OrchestratorDbContext(options);
        await new ComputerUseSessionStore(settleDb).CompleteAsync(winner.PendingOperationId!.Value,
            new(1, winner.PendingOperationId.Value, winner.SessionId, ComputerUseStatuses.Completed), now, default);

        // Independent scoped contexts read the same revision. Optimistic
        // concurrency and the command receipt commit fence the losing writer.
        await using var thirdDb = new OrchestratorDbContext(options);
        await using var fourthDb = new OrchestratorDbContext(options);
        var action = new ComputerUseOperationStart(winner.SessionId, Guid.NewGuid(), winner.TenantId, winner.AgentId,
            winner.Owner, "act", new string('B', 64), null, "fixture-action", now.AddSeconds(1));
        var actions = await Task.WhenAll(new ComputerUseSessionStore(thirdDb).BeginAsync(action, default),
            new ComputerUseSessionStore(fourthDb).BeginAsync(action with { OperationId = Guid.NewGuid() }, default));
        actions.Count(result => result.Dispatch).Should().Be(1);
        actions.Count(result => result.FailureCode is "computer_use_operation_conflict" or "computer_use_operation_pending").Should().Be(1);
        await using var read = new OrchestratorDbContext(options);
        (await read.ComputerUseSessions.CountAsync(row => row.ActiveSlot != null)).Should().Be(1);
        (await read.ComputerUseOperations.CountAsync()).Should().Be(2);
        Func<Task> rewriteAudit = () => read.Database.ExecuteSqlRawAsync("UPDATE \"ComputerUseAudits\" SET \"Outcome\" = 'rewritten'");
        (await rewriteAudit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55000");
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Endpoints.Search;
using NetRatel.API.Services.Terminal;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Terminals;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class SqliteProviderMigrationTests
{
    [Fact]
    public async Task Versioned_sqlite_migrations_preserve_identity_and_case_insensitive_directory_search()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"netratel-sqlite-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Foreign Keys=True";

        try
        {
            var orchestratorOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
                .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"))
                .Options;
            var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
                .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"))
                .Options;

            await using (var identity = new NetRatelIdentityDbContext(identityOptions))
            {
                await identity.Database.MigrateAsync();
                (await identity.Database.GetAppliedMigrationsAsync()).Should().Contain("20260920085950_InitialSqlite");
            }

            await using (var db = new OrchestratorDbContext(orchestratorOptions))
            {
                await db.Database.MigrateAsync();
                (await db.Database.GetAppliedMigrationsAsync()).Should().HaveCount(2);

                var tenant = new Tenant { Name = "SQLite Tenant" };
                db.Tenants.Add(tenant);
                await db.SaveChangesAsync();

                var agentId = Guid.NewGuid();
                db.Agents.Add(new Agent
                {
                    Id = agentId,
                    TenantId = tenant.Id,
                    Name = "field-linux-agent",
                    DeviceInfoJson = """{"os":"Linux"}""",
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();

                var rows = await GlobalSearchEndpoints.BuildAgentQuery(db, "LINUX").ToListAsync();
                rows.Should().ContainSingle(row => row.AgentId == agentId);

                // Exercise every global-search projection against the actual
                // provider, rather than only validating SQLite SQL generation.
                (await GlobalSearchEndpoints.BuildJobQuery(db, "LINUX").ToListAsync()).Should().BeEmpty();
                (await GlobalSearchEndpoints.BuildRequestQuery(db, "LINUX").ToListAsync()).Should().BeEmpty();
                (await GlobalSearchEndpoints.BuildTaskQuery(db, "LINUX").ToListAsync()).Should().BeEmpty();

                var terminalSettings = new ClientTerminalSettingsService(
                    db,
                    NullLogger<ClientTerminalSettingsService>.Instance);
                await terminalSettings.SetOverrideAsync("Field-Linux-Agent", TerminalTransportKind.ApiWebSocket, TestContext.Current.CancellationToken);
                (await terminalSettings.GetOverridesAsync(["field-linux-agent"], TestContext.Current.CancellationToken))
                    .Should().ContainSingle()
                    .Which.Value.Should().Be(TerminalTransportKind.ApiWebSocket);
            }

            await using (var restarted = new OrchestratorDbContext(orchestratorOptions))
            {
                await restarted.Database.MigrateAsync();
                (await restarted.Agents.CountAsync()).Should().Be(1);
            }
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}

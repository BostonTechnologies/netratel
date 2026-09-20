using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Endpoints.Search;
using NetRatel.API.Services.Terminal;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Terminals;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class SqliteProviderMigrationTests
{
    [Fact]
    public async Task Versioned_sqlite_migrations_preserve_identity_and_case_insensitive_directory_search()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"netratel-sqlite-{Guid.NewGuid():N}.db");
        var restoredDatabasePath = Path.Combine(Path.GetTempPath(), $"netratel-sqlite-restored-{Guid.NewGuid():N}.db");
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
                identity.Users.Add(new LocalUser
                {
                    Id = "backup-admin",
                    UserName = "backup-admin@example.test",
                    NormalizedUserName = "BACKUP-ADMIN@EXAMPLE.TEST",
                    Email = "backup-admin@example.test",
                    NormalizedEmail = "BACKUP-ADMIN@EXAMPLE.TEST",
                    PrincipalId = "local:backup-admin",
                    DisplayName = "Backup administrator",
                    SecurityStamp = Guid.NewGuid().ToString("N")
                });
                await identity.SaveChangesAsync();
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

            await using (var backupConnection = new SqliteConnection(connectionString))
            {
                await backupConnection.OpenAsync(TestContext.Current.CancellationToken);
                await using var backup = backupConnection.CreateCommand();
                backup.CommandText = "VACUUM INTO $backupPath";
                backup.Parameters.AddWithValue("$backupPath", restoredDatabasePath);
                await backup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var restoredConnectionString = $"Data Source={restoredDatabasePath};Foreign Keys=True";
            var restoredOrchestratorOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
                .UseSqlite(restoredConnectionString, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"))
                .Options;
            var restoredIdentityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
                .UseSqlite(restoredConnectionString, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"))
                .Options;

            await using (var restoredIdentity = new NetRatelIdentityDbContext(restoredIdentityOptions))
            {
                await restoredIdentity.Database.MigrateAsync();
                (await restoredIdentity.Users.SingleAsync()).PrincipalId.Should().Be("local:backup-admin");
            }

            await using (var restored = new OrchestratorDbContext(restoredOrchestratorOptions))
            {
                await restored.Database.MigrateAsync();
                (await restored.Agents.CountAsync()).Should().Be(1);
                var enrollment = await new EnrollmentCodeIssueService(restored).IssueAsync(
                    new EnrollmentCodeIssueRequest(1, 60, 1, "backup-admin", "restore verification"),
                    TestContext.Current.CancellationToken);
                enrollment.Code.Should().NotBeNullOrWhiteSpace();
            }
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(restoredDatabasePath);
        }
    }
}

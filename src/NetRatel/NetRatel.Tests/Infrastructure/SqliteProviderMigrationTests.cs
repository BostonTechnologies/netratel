using System.Security.Claims;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Endpoints.Search;
using NetRatel.API.Services.Terminal;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Terminals;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class SqliteProviderMigrationTests
{
    [Fact]
    public void Provider_selection_defaults_to_PostgreSql_and_preserves_connection_alias_precedence()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:NetRatelDb"] = "Host=primary;Database=netratel",
                ["ConnectionStrings:Default"] = "Host=compatibility;Database=netratel"
            })
            .Build();

        var database = NetRatelDatabaseConfigurationResolver.Resolve(configuration);

        database.Provider.Should().Be(NetRatelDatabaseProvider.PostgreSql);
        database.ConnectionString.Should().Contain("Host=primary");
    }

    [Theory]
    [InlineData("Data Source=:memory:", null, "durable")]
    [InlineData("Data Source=relative.db", null, "absolute")]
    [InlineData("Data Source=/var/netratel/sqlite/netratel.db", "2", "single")]
    public void Sqlite_selection_rejects_unsupported_storage_or_topology(string connectionString, string? instanceCount, string expectedMessage)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:NetRatelDb"] = connectionString
        };
        if (instanceCount is not null)
        {
            settings["Database:InstanceCount"] = instanceCount;
        }

        var act = () => NetRatelDatabaseConfigurationResolver.Resolve(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public void Sqlite_selection_enforces_a_durable_single_instance_connection()
    {
        var database = NetRatelDatabaseConfigurationResolver.Resolve(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:InstanceCount"] = "1",
                ["ConnectionStrings:Default"] = "Data Source=/var/netratel/sqlite/netratel.db"
            }).Build());

        database.Provider.Should().Be(NetRatelDatabaseProvider.Sqlite);
        var sqlite = new SqliteConnectionStringBuilder(database.ConnectionString);
        sqlite.DataSource.Should().Be("/var/netratel/sqlite/netratel.db");
        sqlite.ForeignKeys.Should().BeTrue();
    }

    [Fact]
    public void Provider_specific_models_do_not_cross_contaminate_a_shared_process()
    {
        using var sqlite = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=/tmp/netratel-model-cache.sqlite", provider =>
                provider.MigrationsAssembly("NetRatel.SqliteMigrations"))
            .Options);
        _ = sqlite.Model;

        using var postgres = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql("Host=localhost;Database=netratel_model_cache;Username=test;Password=test")
            .Options);

        GlobalSearchEndpoints.BuildAgentQuery(postgres, "needle").ToQueryString().Should().Contain("ILIKE");
    }

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
                const string backupPrincipalId = "local:backup-admin";
                identity.Users.Add(new LocalUser
                {
                    Id = "backup-admin",
                    UserName = "backup-admin@example.test",
                    NormalizedUserName = "BACKUP-ADMIN@EXAMPLE.TEST",
                    Email = "backup-admin@example.test",
                    NormalizedEmail = "BACKUP-ADMIN@EXAMPLE.TEST",
                    PrincipalId = backupPrincipalId,
                    DisplayName = "Backup administrator",
                    SecurityStamp = Guid.NewGuid().ToString("N")
                });
                var observer = new AccessRole { Id = "backup-observer", Name = "Backup observer", DelegationRank = 10 };
                observer.Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.TelemetryRead });
                identity.AccessRoles.Add(observer);
                identity.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment
                {
                    PrincipalId = backupPrincipalId,
                    RoleId = observer.Id,
                    TenantId = 1
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

                var createdAtUtc = DateTimeOffset.UtcNow;
                var earlierAgentId = Guid.NewGuid();
                var agentId = Guid.NewGuid();
                db.Agents.Add(new Agent
                {
                    Id = earlierAgentId,
                    TenantId = tenant.Id,
                    Name = "earlier-field-agent",
                    CreatedAtUtc = createdAtUtc.AddMinutes(-1)
                });
                db.Agents.Add(new Agent
                {
                    Id = agentId,
                    TenantId = tenant.Id,
                    Name = "field-linux-agent",
                    DeviceInfoJson = """{"os":"Linux"}""",
                    CreatedAtUtc = createdAtUtc
                });
                await db.SaveChangesAsync();

                (await db.Agents.AsNoTracking().OrderBy(agent => agent.CreatedAtUtc).Take(2)
                    .Select(agent => agent.Id).ToArrayAsync()).Should().Equal(earlierAgentId, agentId);

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
                (await restarted.Agents.CountAsync()).Should().Be(2);
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
                var access = new EffectiveAccessService(restoredIdentity, new ConfigurationBuilder().Build());
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("netratel_principal_id", "local:backup-admin")], "local"));
                (await access.AuthorizeAsync(principal, NetRatelPermissions.TelemetryRead, tenantId: 1)).Should().BeTrue();
                (await access.AuthorizeAsync(principal, NetRatelPermissions.TelemetryRead, tenantId: 2)).Should().BeFalse();
            }

            await using (var restored = new OrchestratorDbContext(restoredOrchestratorOptions))
            {
                await restored.Database.MigrateAsync();
                (await restored.Agents.CountAsync()).Should().Be(2);
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

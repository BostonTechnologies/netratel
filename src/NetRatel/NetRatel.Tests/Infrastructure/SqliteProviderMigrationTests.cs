using System.Security.Claims;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetRatel.API.Bootstrap;
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
    public async Task Bootstrap_initializer_creates_one_local_administrator_and_tenant_then_rejects_replay()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"netratel-bootstrap-sqlite-{Guid.NewGuid():N}.db");
        var stateDirectory = Path.Combine(Path.GetTempPath(), "netratel-bootstrap-initialization", Guid.NewGuid().ToString("N"));
        var connectionString = $"Data Source={databasePath};Foreign Keys=True";
        try
        {
            var applicationOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
                .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"))
                .Options;
            var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
                .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"))
                .Options;
            await using (var application = new OrchestratorDbContext(applicationOptions)) await application.Database.MigrateAsync();
            await using (var identity = new NetRatelIdentityDbContext(identityOptions)) await identity.Database.MigrateAsync();

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:InstanceCount"] = "1",
                ["ConnectionStrings:NetRatelDb"] = connectionString
            }).Build();
            var store = new BootstrapStateStore(new BootstrapOptions { StateDirectory = stateDirectory });
            await store.LoadOrCreateAsync();
            var proof = await File.ReadAllTextAsync(Path.Combine(stateDirectory, "setup-proof"));
            var claim = await store.ClaimSetupAsync(proof, "SQLite", "ConnectionStrings:NetRatelDb");
            claim.Succeeded.Should().BeTrue();
            claim.Descriptor!.OperationId.Should().NotBeNull();

            var initializer = new BootstrapInitializationService(
                store,
                configuration,
                new PasswordHasher<LocalUser>(),
                Options.Create(new IdentityOptions
                {
                    Password =
                    {
                        RequiredLength = 15,
                        RequiredUniqueChars = 1,
                        RequireDigit = false,
                        RequireLowercase = false,
                        RequireUppercase = false,
                        RequireNonAlphanumeric = false
                    }
                }));
            var initialized = await initializer.InitializeAsync(
                claim.Descriptor.OperationId!.Value,
                new BootstrapInitializationRequest("Initial Administrator", "admin@example.test", "a local-first passphrase", "Initial tenant"));

            initialized.Succeeded.Should().BeTrue();
            (await store.LoadOrCreateAsync()).State.Should().Be(BootstrapState.Ready);
            var retry = await initializer.InitializeAsync(
                claim.Descriptor.OperationId.Value,
                new BootstrapInitializationRequest("Other", "other@example.test", "a local-first passphrase", "Other tenant"));
            retry.Succeeded.Should().BeTrue();
            retry.TenantId.Should().Be(initialized.TenantId);
            retry.UserId.Should().Be(initialized.UserId);

            await using var verifyApplication = new OrchestratorDbContext(applicationOptions);
            await using var verifyIdentity = new NetRatelIdentityDbContext(identityOptions);
            (await verifyApplication.Tenants.SingleAsync()).Name.Should().Be("Initial tenant");
            var initialization = await verifyApplication.BootstrapInitializations.SingleAsync();
            initialization.Id.Should().Be(BootstrapInitializationRecord.SingletonId);
            initialization.TenantId.Should().Be((await verifyApplication.Tenants.SingleAsync()).Id);
            var user = await verifyIdentity.Users.SingleAsync();
            user.Email.Should().Be("admin@example.test");
            user.IsInstanceAdministrator.Should().BeTrue();
            user.PrincipalId.Should().NotBeNullOrWhiteSpace();
            initialization.AdministratorUserId.Should().Be(user.Id);

            var originalStamp = user.SecurityStamp;
            var originalRevision = user.AuthorizationRevision;
            var recovered = await initializer.RecoverAdministratorAsync("admin@example.test", "a recovered local passphrase");
            recovered.Succeeded.Should().BeTrue();

            await verifyIdentity.Entry(user).ReloadAsync();
            user.SecurityStamp.Should().NotBe(originalStamp);
            user.AuthorizationRevision.Should().Be(originalRevision + 1);
            user.TwoFactorEnabled.Should().BeFalse();
            new PasswordHasher<LocalUser>().VerifyHashedPassword(user, user.PasswordHash!, "a recovered local passphrase")
                .Should().Be(PasswordVerificationResult.Success);
            (await initializer.RecoverAdministratorAsync("not-an-admin@example.test", "another recovered passphrase")).Succeeded.Should().BeFalse();
        }
        finally
        {
            File.Delete(databasePath);
            if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Restart_reconciles_a_committed_initialization_after_the_descriptor_completion_crash_window()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"netratel-bootstrap-reconciliation-{Guid.NewGuid():N}.db");
        var stateDirectory = Path.Combine(Path.GetTempPath(), "netratel-bootstrap-reconciliation", Guid.NewGuid().ToString("N"));
        var connectionString = $"Data Source={databasePath};Foreign Keys=True";
        try
        {
            var applicationOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
                .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"))
                .Options;
            var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
                .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"))
                .Options;
            await using (var application = new OrchestratorDbContext(applicationOptions)) await application.Database.MigrateAsync();
            await using (var identity = new NetRatelIdentityDbContext(identityOptions)) await identity.Database.MigrateAsync();

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:InstanceCount"] = "1",
                ["ConnectionStrings:NetRatelDb"] = connectionString
            }).Build();
            var store = new BootstrapStateStore(new BootstrapOptions { StateDirectory = stateDirectory });
            await store.LoadOrCreateAsync();
            var proof = await File.ReadAllTextAsync(Path.Combine(stateDirectory, "setup-proof"));
            var claim = await store.ClaimSetupAsync(proof, "SQLite", "ConnectionStrings:NetRatelDb");
            claim.Succeeded.Should().BeTrue();
            var descriptor = claim.Descriptor!;

            // This is the durable state left if the process stops after the shared database
            // transaction commits and before CompleteSetupAsync can update descriptor.json.
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var recoveryApplication = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
                .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"))
                .Options);
            await using var recoveryIdentity = new NetRatelIdentityDbContext(new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
                .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"))
                .Options);
            await using var transaction = await recoveryIdentity.Database.BeginTransactionAsync();
            await recoveryApplication.Database.UseTransactionAsync(transaction.GetDbTransaction());

            var principal = new ApplicationPrincipal { Id = "recovered-bootstrap-principal" };
            var user = new LocalUser
            {
                Id = "recovered-bootstrap-admin",
                UserName = "admin@example.test",
                NormalizedUserName = "ADMIN@EXAMPLE.TEST",
                Email = "admin@example.test",
                NormalizedEmail = "ADMIN@EXAMPLE.TEST",
                PrincipalId = principal.Id,
                DisplayName = "Initial Administrator",
                IsEnabled = true,
                IsInstanceAdministrator = true
            };
            principal.LocalUserId = user.Id;
            var tenant = new Tenant
            {
                Name = "Initial tenant",
                ContactPerson = user.DisplayName,
                ContactEmail = user.Email,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            recoveryIdentity.ApplicationPrincipals.Add(principal);
            recoveryIdentity.Users.Add(user);
            recoveryApplication.Tenants.Add(tenant);
            await recoveryIdentity.SaveChangesAsync();
            await recoveryApplication.SaveChangesAsync();
            recoveryApplication.BootstrapInitializations.Add(new BootstrapInitializationRecord
            {
                BootstrapInstanceId = descriptor.InstanceId,
                OperationId = descriptor.OperationId!.Value,
                TenantId = tenant.Id,
                AdministratorUserId = user.Id,
                CompletedAtUtc = DateTimeOffset.UtcNow
            });
            await recoveryApplication.SaveChangesAsync();
            await transaction.CommitAsync();

            // A later restart can encounter the expired configuration lease before the
            // descriptor completion write. That recovery reason alone is eligible for the
            // durable-record reconciliation; key-material and other recovery reasons are not.
            await store.UpdateAsync(current => current with
            {
                State = BootstrapState.RecoveryRequired,
                OperationId = null,
                OperationLeaseExpiresAtUtc = null,
                RecoveryReason = "configuration-lease-expired"
            }, "configuration-lease-expired");

            var reconciled = await new BootstrapLifecycleService(store, configuration).InitializeAsync();

            reconciled.State.Should().Be(BootstrapState.Ready);
            reconciled.OperationId.Should().BeNull();
            reconciled.RecoveryReason.Should().BeNull();
            (await store.LoadOrCreateAsync()).State.Should().Be(BootstrapState.Ready);
        }
        finally
        {
            File.Delete(databasePath);
            if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, recursive: true);
        }
    }

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
                (await identity.Database.GetAppliedMigrationsAsync()).Should().Contain("20260920121211_AddIntegrationCredentials");
                (await identity.Database.GetAppliedMigrationsAsync()).Should().Contain("20260920170708_AddDeploymentBranding");
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
                (await db.Database.GetAppliedMigrationsAsync()).Should().HaveCount(6)
                    .And.Contain(migration => migration.EndsWith("AddBootstrapInitializationRecord", StringComparison.Ordinal))
                    .And.Contain(migration => migration.EndsWith("AddIntegrationCredentialInstanceGrants", StringComparison.Ordinal));

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

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetRatel.API.Bootstrap;
using NetRatel.API.Endpoints.Search;
using NetRatel.API.Services.Terminal;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Terminals;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class PostgreSqlProviderRegressionTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Bootstrap_initializer_creates_one_local_administrator_and_tenant_then_rejects_replay_and_recovers_the_account()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var stateDirectory = Path.Combine(Path.GetTempPath(), "netratel-bootstrap-postgres", Guid.NewGuid().ToString("N"));
        try
        {
            var applicationOptions = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connectionString).Options;
            var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connectionString).Options;
            await using (var application = new OrchestratorDbContext(applicationOptions)) await application.Database.MigrateAsync();
            await using (var identity = new NetRatelIdentityDbContext(identityOptions)) await identity.Database.MigrateAsync();

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "PostgreSql",
                ["ConnectionStrings:NetRatelDb"] = connectionString
            }).Build();
            var store = new BootstrapStateStore(new BootstrapOptions { StateDirectory = stateDirectory });
            await store.LoadOrCreateAsync();
            var proof = await File.ReadAllTextAsync(Path.Combine(stateDirectory, "setup-proof"));
            var claim = await store.ClaimSetupAsync(proof, "PostgreSQL", "ConnectionStrings:NetRatelDb");
            claim.Succeeded.Should().BeTrue();
            var initializer = new BootstrapInitializationService(store, configuration, new PasswordHasher<LocalUser>(),
                Options.Create(BootstrapApplicationExtensions.CreateBootstrapIdentityOptions()));

            var initialized = await initializer.InitializeAsync(claim.Descriptor!.OperationId!.Value,
                new BootstrapInitializationRequest("Initial Administrator", "admin@example.test", "a local-first passphrase", "Initial tenant"));
            initialized.Succeeded.Should().BeTrue();
            (await store.LoadOrCreateAsync()).State.Should().Be(BootstrapState.Ready);
            var retry = await initializer.InitializeAsync(claim.Descriptor.OperationId.Value,
                new BootstrapInitializationRequest("Other", "other@example.test", "a local-first passphrase", "Other tenant"));
            retry.Succeeded.Should().BeTrue();
            retry.TenantId.Should().Be(initialized.TenantId);
            retry.UserId.Should().Be(initialized.UserId);

            await using var verifyApplication = new OrchestratorDbContext(applicationOptions);
            await using var verifyIdentity = new NetRatelIdentityDbContext(identityOptions);
            (await verifyApplication.Tenants.SingleAsync()).Name.Should().Be("Initial tenant");
            var initialization = await verifyApplication.BootstrapInitializations.SingleAsync();
            initialization.Id.Should().Be(BootstrapInitializationRecord.SingletonId);
            initialization.TenantId.Should().Be(initialized.TenantId);
            var user = await verifyIdentity.Users.SingleAsync();
            user.Email.Should().Be("admin@example.test");
            user.IsInstanceAdministrator.Should().BeTrue();
            initialization.AdministratorUserId.Should().Be(user.Id);

            var originalStamp = user.SecurityStamp;
            var originalRevision = user.AuthorizationRevision;
            var credentialService = new IntegrationCredentialService(verifyIdentity);
            var credential = await credentialService.CreateAsync(user.PrincipalId, new IntegrationCredentialCreateRequest(
                "Before recovery", IntegrationCredentialPurpose.Api, DateTimeOffset.UtcNow.AddDays(7),
                [new IntegrationCredentialGrantRequest(initialized.TenantId!.Value, NetRatelPermissions.TelemetryRead)]));
            (await credentialService.VerifyAsync(credential.Secret, IntegrationCredentialPurpose.Api)).Should().NotBeNull();
            user.LockoutEnd = DateTimeOffset.UtcNow.AddDays(1);
            user.AccessFailedCount = 5;
            user.TwoFactorEnabled = true;
            await verifyIdentity.SaveChangesAsync();
            (await initializer.RecoverAdministratorAsync("admin@example.test", "a recovered local passphrase")).Succeeded.Should().BeTrue();
            await verifyIdentity.Entry(user).ReloadAsync();
            user.SecurityStamp.Should().NotBe(originalStamp);
            user.AuthorizationRevision.Should().Be(originalRevision + 1);
            user.TwoFactorEnabled.Should().BeFalse();
            user.LockoutEnd.Should().BeNull();
            user.AccessFailedCount.Should().Be(0);
            (await credentialService.VerifyAsync(credential.Secret, IntegrationCredentialPurpose.Api)).Should().BeNull();
            (await verifyIdentity.IntegrationCredentials.CountAsync(value => value.RevokedAtUtc != null)).Should().Be(1);
            new PasswordHasher<LocalUser>().VerifyHashedPassword(user, user.PasswordHash!, "a recovered local passphrase")
                .Should().Be(PasswordVerificationResult.Success);
            (await initializer.RecoverAdministratorAsync("not-an-admin@example.test", "another recovered passphrase")).Succeeded.Should().BeFalse();

            var lifecycle = new BootstrapLifecycleService(store, configuration);
            (await lifecycle.InitializeAsync()).State.Should().Be(BootstrapState.Ready);
            verifyApplication.BootstrapInitializations.Remove(initialization);
            await verifyApplication.SaveChangesAsync();
            var partialReset = await lifecycle.InitializeAsync();
            partialReset.State.Should().Be(BootstrapState.RecoveryRequired);
            partialReset.RecoveryReason.Should().Be("ready-continuity-missing");
            (await lifecycle.ClaimSetupAsync(proof)).Succeeded.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public void PostgreSql_alias_precedence_is_deterministic_and_legacy_provider_is_rejected_before_file_creation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:NetRatelDb"] = "Host=primary;Database=netratel",
            ["ConnectionStrings:Default"] = "Host=compatibility;Database=netratel"
        }).Build();
        NetRatelDatabaseConfigurationResolver.Resolve(configuration).ConnectionString.Should().Contain("Host=primary");

        var filePath = Path.Combine(Path.GetTempPath(), "netratel-rejected-sqlite-" + Guid.NewGuid().ToString("N") + ".db");
        var legacy = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:NetRatelDb"] = $"Data Source={filePath}"
        }).Build();
        var action = () => NetRatelDatabaseConfigurationResolver.Resolve(legacy);
        action.Should().Throw<InvalidOperationException>().WithMessage("*SQLite application storage is retired*");
        File.Exists(filePath).Should().BeFalse();

        var unknown = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "unknown",
            ["ConnectionStrings:NetRatelDb"] = "Host=primary;Database=netratel"
        }).Build();
        Action rejectUnknown = () => NetRatelDatabaseConfigurationResolver.Resolve(unknown);
        rejectUnknown.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be PostgreSql*");
    }

    [Fact]
    public async Task PostgreSql_migrations_preserve_identity_scope_search_sorting_and_terminal_settings_after_restart()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var applicationOptions = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connectionString).Options;
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connectionString).Options;
        await using (var db = new OrchestratorDbContext(applicationOptions)) await db.Database.MigrateAsync();
        await using (var identity = new NetRatelIdentityDbContext(identityOptions)) await identity.Database.MigrateAsync();

        const string principalId = "local:backup-admin";
        await using (var identity = new NetRatelIdentityDbContext(identityOptions))
        {
            identity.ApplicationPrincipals.Add(new ApplicationPrincipal { Id = principalId, LocalUserId = "backup-admin" });
            identity.Users.Add(new LocalUser
            {
                Id = "backup-admin", UserName = "backup-admin@example.test", NormalizedUserName = "BACKUP-ADMIN@EXAMPLE.TEST",
                Email = "backup-admin@example.test", NormalizedEmail = "BACKUP-ADMIN@EXAMPLE.TEST", PrincipalId = principalId,
                DisplayName = "Backup administrator", SecurityStamp = Guid.NewGuid().ToString("N")
            });
            var observer = new AccessRole { Id = "backup-observer", Name = "Backup observer", DelegationRank = 10 };
            observer.Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.TelemetryRead });
            identity.AccessRoles.Add(observer);
            identity.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment { PrincipalId = principalId, RoleId = observer.Id, TenantId = 1 });
            await identity.SaveChangesAsync();
        }

        var earlierId = Guid.NewGuid();
        var matchingId = Guid.NewGuid();
        await using (var db = new OrchestratorDbContext(applicationOptions))
        {
            var tenant = new Tenant
            {
                Name = "PostgreSQL tenant", ContactPerson = "Backup administrator", ContactEmail = "backup-admin@example.test",
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow, AutoUpdateChannel = "stable", Version = 1
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            var now = DateTimeOffset.UtcNow;
            db.Agents.Add(new Agent { Id = earlierId, TenantId = tenant.Id, Name = "earlier-field-agent", CreatedAtUtc = now.AddMinutes(-1) });
            db.Agents.Add(new Agent { Id = matchingId, TenantId = tenant.Id, Name = "field-linux-agent", DeviceInfoJson = """{"os":"Linux"}""", CreatedAtUtc = now });
            await db.SaveChangesAsync();
            (await db.Agents.AsNoTracking().OrderBy(agent => agent.CreatedAtUtc).Select(agent => agent.Id).ToArrayAsync())
                .Should().Equal(earlierId, matchingId);
            (await GlobalSearchEndpoints.BuildAgentQuery(db, "LINUX").ToListAsync()).Should().ContainSingle(row => row.AgentId == matchingId);
            (await GlobalSearchEndpoints.BuildJobQuery(db, "LINUX").ToListAsync()).Should().BeEmpty();
            (await GlobalSearchEndpoints.BuildRequestQuery(db, "LINUX").ToListAsync()).Should().BeEmpty();
            (await GlobalSearchEndpoints.BuildTaskQuery(db, "LINUX").ToListAsync()).Should().BeEmpty();
            var terminal = new ClientTerminalSettingsService(db, NullLogger<ClientTerminalSettingsService>.Instance);
            await terminal.SetOverrideAsync("Field-Linux-Agent", TerminalTransportKind.ApiWebSocket, TestContext.Current.CancellationToken);
        }

        await using var restarted = new OrchestratorDbContext(applicationOptions);
        await restarted.Database.MigrateAsync();
        (await restarted.Agents.CountAsync()).Should().Be(2);
        var restoredTerminal = new ClientTerminalSettingsService(restarted, NullLogger<ClientTerminalSettingsService>.Instance);
        (await restoredTerminal.GetOverridesAsync(["field-linux-agent"], TestContext.Current.CancellationToken))
            .Should().ContainSingle().Which.Value.Should().Be(TerminalTransportKind.ApiWebSocket);
        await using var restartedIdentity = new NetRatelIdentityDbContext(identityOptions);
        (await restartedIdentity.Users.SingleAsync()).PrincipalId.Should().Be(principalId);
        var access = new EffectiveAccessService(restartedIdentity, new ConfigurationBuilder().Build());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", principalId)], "local"));
        (await access.AuthorizeAsync(principal, NetRatelPermissions.TelemetryRead, tenantId: 1)).Should().BeTrue();
        (await access.AuthorizeAsync(principal, NetRatelPermissions.TelemetryRead, tenantId: 2)).Should().BeFalse();
    }
}

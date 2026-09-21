using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using NetRatel.API.Bootstrap;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class BootstrapLegacyAdoptionPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "netratel-bootstrap-adoption-tests", Guid.NewGuid().ToString("N"));

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            CREATE SCHEMA legacy;
            CREATE TABLE legacy."Tenants" ("Id" integer primary key);
            CREATE TABLE legacy."Agents" ("Id" uuid primary key);
            INSERT INTO legacy."Tenants" ("Id") VALUES (1);
            INSERT INTO legacy."Agents" ("Id") VALUES ('00000000-0000-0000-0000-000000000001');
            CREATE SCHEMA empty_bootstrap;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_stateRoot))
        {
            Directory.Delete(_stateRoot, true);
        }
    }

    [Fact]
    public async Task Empty_schema_is_not_adopted_but_existing_netratel_evidence_is_adopted()
    {
        var empty = await InitializeAsync("empty_bootstrap", includeOidc: false);
        empty.State.Should().Be(BootstrapState.Unconfigured);

        var configuredOidc = await InitializeAsync("empty_bootstrap", includeOidc: true);
        configuredOidc.State.Should().Be(BootstrapState.Ready);
        configuredOidc.AdoptedExistingInstallation.Should().BeFalse();

        var adopted = await InitializeAsync("legacy", includeOidc: true);
        adopted.State.Should().Be(BootstrapState.Ready);
        adopted.AdoptedExistingInstallation.Should().BeTrue();
        adopted.SelectedProvider.Should().Be("PostgreSQL");
        adopted.ConnectionReference.Should().Be("ConnectionStrings:NetRatelDb");
    }

    [Fact]
    public async Task Expired_setup_lease_reconciles_only_the_matching_committed_initialization()
    {
        var connectionString = _postgres.GetConnectionString();
        var applicationOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var application = new OrchestratorDbContext(applicationOptions)) await application.Database.MigrateAsync();
        await using (var identity = new NetRatelIdentityDbContext(identityOptions)) await identity.Database.MigrateAsync();

        var stateDirectory = Path.Combine(_stateRoot, "transaction-recovery");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:NetRatelDb"] = connectionString
        }).Build();
        var store = new BootstrapStateStore(new BootstrapOptions { StateDirectory = stateDirectory });
        await store.LoadOrCreateAsync();
        var proof = await File.ReadAllTextAsync(Path.Combine(stateDirectory, "setup-proof"));
        var claim = await store.ClaimSetupAsync(proof, "PostgreSQL", "ConnectionStrings:NetRatelDb");
        claim.Succeeded.Should().BeTrue();
        var descriptor = claim.Descriptor!;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var applicationTransactionContext = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(connection)
            .Options);
        await using var identityTransactionContext = new NetRatelIdentityDbContext(new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseNpgsql(connection)
            .Options);
        await using var transaction = await identityTransactionContext.Database.BeginTransactionAsync();
        await applicationTransactionContext.Database.UseTransactionAsync(transaction.GetDbTransaction());

        var principal = new ApplicationPrincipal { Id = "postgres-bootstrap-principal" };
        var administrator = new LocalUser
        {
            Id = "postgres-bootstrap-admin",
            UserName = "admin@example.test",
            NormalizedUserName = "ADMIN@EXAMPLE.TEST",
            Email = "admin@example.test",
            NormalizedEmail = "ADMIN@EXAMPLE.TEST",
            PrincipalId = principal.Id,
            DisplayName = "Initial Administrator",
            IsEnabled = true,
            IsInstanceAdministrator = true
        };
        principal.LocalUserId = administrator.Id;
        var tenant = new Tenant
        {
            Name = "Initial tenant",
            ContactPerson = administrator.DisplayName,
            ContactEmail = administrator.Email,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            AutoUpdateChannel = "stable",
            Version = 1
        };
        identityTransactionContext.ApplicationPrincipals.Add(principal);
        identityTransactionContext.Users.Add(administrator);
        applicationTransactionContext.Tenants.Add(tenant);
        await identityTransactionContext.SaveChangesAsync();
        await applicationTransactionContext.SaveChangesAsync();
        applicationTransactionContext.BootstrapInitializations.Add(new BootstrapInitializationRecord
        {
            BootstrapInstanceId = descriptor.InstanceId,
            OperationId = descriptor.OperationId!.Value,
            TenantId = tenant.Id,
            AdministratorUserId = administrator.Id,
            CompletedAtUtc = DateTimeOffset.UtcNow
        });
        await applicationTransactionContext.SaveChangesAsync();
        await transaction.CommitAsync();

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
    }

    private Task<BootstrapDescriptor> InitializeAsync(string schema, bool includeOidc)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:NetRatelDb"] = _postgres.GetConnectionString() + $";Search Path={schema}"
        };
        if (includeOidc)
        {
            values["Authentication:Oidc:Authority"] = "https://issuer.example.test";
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var options = new BootstrapOptions { StateDirectory = Path.Combine(_stateRoot, schema) };
        return new BootstrapLifecycleService(new BootstrapStateStore(options), configuration).InitializeAsync();
    }
}

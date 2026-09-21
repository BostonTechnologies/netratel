using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;
using Npgsql;

namespace NetRatel.API.Bootstrap;

/// <summary>
/// Reconciles lifecycle evidence before the operational service graph is assembled. It deliberately
/// never migrates, creates a user, or changes a storage provider: those are later phase operations.
/// </summary>
public sealed class BootstrapLifecycleService
{
    private readonly BootstrapStateStore _store;
    private readonly IConfiguration _configuration;

    public BootstrapLifecycleService(BootstrapStateStore store, IConfiguration configuration)
    {
        _store = store;
        _configuration = configuration;
    }

    public async Task<BootstrapDescriptor> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var descriptor = await _store.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var connectionString = _configuration.GetConnectionString("NetRatelDb") ?? _configuration.GetConnectionString("Default");
        if (IsPlaceholder(connectionString))
        {
            return descriptor;
        }

        var database = NetRatelDatabaseConfigurationResolver.Resolve(_configuration);

        if (CanReconcileCompletedInitialization(descriptor))
        {
            try
            {
                if (await HasCompletedInitializationAsync(database, descriptor, cancellationToken).ConfigureAwait(false))
                {
                    return await _store.UpdateAsync(
                        current => current with
                        {
                            State = BootstrapState.Ready,
                            SelectedProvider = ProviderName(database.Provider),
                            ConnectionReference = "ConnectionStrings:NetRatelDb",
                            OperationId = null,
                            OperationLeaseExpiresAtUtc = null,
                            RecoveryReason = null,
                            AdoptedExistingInstallation = false
                        },
                        "initialization-commit-reconciled",
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or SqliteException or TimeoutException or InvalidOperationException)
            {
                // An unavailable database cannot establish completed initialization evidence.
                // Preserve the restricted bootstrap state and retry reconciliation on startup.
                return descriptor;
            }

            return descriptor;
        }

        if (descriptor.State != BootstrapState.Unconfigured)
        {
            return descriptor;
        }

        LegacyProbeResult probe;
        try
        {
            probe = await ProbeExistingInstallationAsync(database, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or SqliteException or TimeoutException or InvalidOperationException)
        {
            return await _store.UpdateAsync(
                current => current with { State = BootstrapState.RecoveryRequired, OperationId = null, OperationLeaseExpiresAtUtc = null, RecoveryReason = "configured-storage-unavailable" },
                "configured-storage-unavailable",
                cancellationToken).ConfigureAwait(false);
        }

        if (probe == LegacyProbeResult.EmptyOrUnknown)
        {
            // A complete deployment-owned OIDC configuration is an explicit request to retain
            // the pre-local-first operational mode. This is not legacy-data adoption: no owner,
            // tenant, or application record is inferred from an empty schema.
            if (HasConfiguredOidc())
            {
                return await _store.UpdateAsync(
                    current => current with
                    {
                        State = BootstrapState.Ready,
                        SelectedProvider = ProviderName(database.Provider),
                        ConnectionReference = "ConnectionStrings:NetRatelDb",
                        OperationId = null,
                        OperationLeaseExpiresAtUtc = null,
                        RecoveryReason = null,
                        AdoptedExistingInstallation = false
                    },
                    "configured-oidc-compatible-startup",
                    cancellationToken).ConfigureAwait(false);
            }

            return descriptor;
        }

        if (probe == LegacyProbeResult.RequiresRecovery || !HasConfiguredOidc())
        {
            return await _store.UpdateAsync(
                current => current with { State = BootstrapState.RecoveryRequired, OperationId = null, OperationLeaseExpiresAtUtc = null, RecoveryReason = "legacy-continuity-insufficient" },
                "legacy-continuity-insufficient",
                cancellationToken).ConfigureAwait(false);
        }

        return await _store.UpdateAsync(
            current => current with
            {
                State = BootstrapState.Ready,
                SelectedProvider = ProviderName(database.Provider),
                ConnectionReference = "ConnectionStrings:NetRatelDb",
                OperationId = null,
                OperationLeaseExpiresAtUtc = null,
                RecoveryReason = null,
                AdoptedExistingInstallation = true
            },
            "legacy-installation-adopted",
            cancellationToken).ConfigureAwait(false);
    }

    public Task<BootstrapClaimResult> ClaimSetupAsync(string proof, CancellationToken cancellationToken = default)
    {
        var hasConfiguredDatabase = !IsPlaceholder(_configuration.GetConnectionString("NetRatelDb") ?? _configuration.GetConnectionString("Default"));
        var provider = hasConfiguredDatabase
            ? ProviderName(NetRatelDatabaseConfigurationResolver.Resolve(_configuration).Provider)
            : null;
        return _store.ClaimSetupAsync(
            proof,
            provider,
            hasConfiguredDatabase ? "ConnectionStrings:NetRatelDb" : null,
            cancellationToken);
    }

    public static BootstrapStatus ToStatus(BootstrapDescriptor descriptor) => new(
        descriptor.State,
        descriptor.State is BootstrapState.Unconfigured or BootstrapState.Configuring,
        descriptor.State == BootstrapState.Ready,
        descriptor.State == BootstrapState.RecoveryRequired,
        descriptor.SelectedProvider,
        descriptor.OperationId);

    private async Task<LegacyProbeResult> ProbeExistingInstallationAsync(NetRatelDatabaseConfiguration database, CancellationToken cancellationToken)
    {
        return database.Provider is NetRatelDatabaseProvider.Sqlite
            ? await ProbeSqliteAsync(database.ConnectionString, cancellationToken).ConfigureAwait(false)
            : await ProbePostgreSqlAsync(database.ConnectionString, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LegacyProbeResult> ProbePostgreSqlAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema()", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }

        return await AssessContinuityAsync(tables, table => HasRowsAsync(connection, table, cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<bool> HasRowsAsync(NpgsqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM \"{table}\")", connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    private static async Task<LegacyProbeResult> ProbeSqliteAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tables.Add(reader.GetString(0));
        }

        return await AssessContinuityAsync(tables, table => HasRowsAsync(connection, table, cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<bool> HasRowsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT EXISTS (SELECT 1 FROM \"{table}\")";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private static async Task<LegacyProbeResult> AssessContinuityAsync(
        ISet<string> tables,
        Func<string, Task<bool>> hasRowsAsync)
    {
        if (!tables.Contains("Tenants") || !await hasRowsAsync("Tenants").ConfigureAwait(false)) return LegacyProbeResult.EmptyOrUnknown;

        foreach (var table in new[] { "Agents", "AgentCredentials", "AgentRefreshTokens", "OidcSigningKeys", "OutboxMessages", "JobRuns", "Requests" }.Where(tables.Contains))
            if (await hasRowsAsync(table).ConfigureAwait(false)) return LegacyProbeResult.Adoptable;

        return LegacyProbeResult.RequiresRecovery;
    }

    private static bool CanReconcileCompletedInitialization(BootstrapDescriptor descriptor) =>
        descriptor.State == BootstrapState.Configuring ||
        (descriptor.State == BootstrapState.RecoveryRequired &&
         string.Equals(descriptor.RecoveryReason, "configuration-lease-expired", StringComparison.Ordinal));

    private static async Task<bool> HasCompletedInitializationAsync(
        NetRatelDatabaseConfiguration database,
        BootstrapDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        await using DbConnection connection = database.Provider is NetRatelDatabaseProvider.Sqlite
            ? new SqliteConnection(database.ConnectionString)
            : new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var applicationOptions = new DbContextOptionsBuilder<OrchestratorDbContext>();
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>();
        if (database.Provider is NetRatelDatabaseProvider.Sqlite)
        {
            applicationOptions.UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"));
            identityOptions.UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"));
        }
        else
        {
            applicationOptions.UseNpgsql(connection, postgres => postgres.MigrationsAssembly("NetRatel.Migrations"));
            identityOptions.UseNpgsql(connection, postgres => postgres.MigrationsAssembly("NetRatel.Migrations"));
        }

        await using var application = new OrchestratorDbContext(applicationOptions.Options);
        var initialization = await application.BootstrapInitializations.AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == BootstrapInitializationRecord.SingletonId &&
                                      record.BootstrapInstanceId == descriptor.InstanceId,
                cancellationToken).ConfigureAwait(false);
        if (initialization is null ||
            !await application.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == initialization.TenantId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await using var identity = new NetRatelIdentityDbContext(identityOptions.Options);
        var administrator = await identity.Users.AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == initialization.AdministratorUserId &&
                                      user.IsEnabled && user.IsInstanceAdministrator,
                cancellationToken).ConfigureAwait(false);
        if (administrator is null)
        {
            return false;
        }

        return await identity.ApplicationPrincipals.AsNoTracking().AnyAsync(
            principal => principal.Id == administrator.PrincipalId && principal.LocalUserId == administrator.Id,
            cancellationToken).ConfigureAwait(false);
    }

    private static string ProviderName(NetRatelDatabaseProvider provider) =>
        provider is NetRatelDatabaseProvider.Sqlite ? "SQLite" : "PostgreSQL";

    private bool HasConfiguredOidc()
    {
        var oidc = _configuration.GetSection("Authentication:Oidc");
        if (oidc.Exists() && !IsPlaceholder(oidc["Authority"]))
        {
            return true;
        }

        return !IsPlaceholder(_configuration["AzureAd:TenantId"]) && !IsPlaceholder(_configuration["AzureAd:ClientId"]);
    }

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains("replace-at-deployment", StringComparison.OrdinalIgnoreCase) || value.Contains("REPLACE_ME", StringComparison.OrdinalIgnoreCase);

    private enum LegacyProbeResult
    {
        EmptyOrUnknown,
        Adoptable,
        RequiresRecovery
    }
}

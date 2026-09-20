using Microsoft.Extensions.Configuration;
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
        if (descriptor.State != BootstrapState.Unconfigured)
        {
            return descriptor;
        }

        var connectionString = _configuration.GetConnectionString("NetRatelDb") ?? _configuration.GetConnectionString("Default");
        if (IsPlaceholder(connectionString))
        {
            return descriptor;
        }

        LegacyProbeResult probe;
        try
        {
            probe = await ProbeExistingInstallationAsync(connectionString!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return await _store.UpdateAsync(
                current => current with { State = BootstrapState.RecoveryRequired, OperationId = null, OperationLeaseExpiresAtUtc = null },
                "configured-storage-unavailable",
                cancellationToken).ConfigureAwait(false);
        }

        if (probe == LegacyProbeResult.EmptyOrUnknown)
        {
            return descriptor;
        }

        if (probe == LegacyProbeResult.RequiresRecovery || !HasConfiguredOidc())
        {
            return await _store.UpdateAsync(
                current => current with { State = BootstrapState.RecoveryRequired, OperationId = null, OperationLeaseExpiresAtUtc = null },
                "legacy-continuity-insufficient",
                cancellationToken).ConfigureAwait(false);
        }

        return await _store.UpdateAsync(
            current => current with
            {
                State = BootstrapState.Ready,
                SelectedProvider = "PostgreSQL",
                ConnectionReference = "ConnectionStrings:NetRatelDb",
                OperationId = null,
                OperationLeaseExpiresAtUtc = null,
                AdoptedExistingInstallation = true
            },
            "legacy-installation-adopted",
            cancellationToken).ConfigureAwait(false);
    }

    public Task<BootstrapClaimResult> ClaimSetupAsync(string proof, CancellationToken cancellationToken = default)
    {
        var hasConfiguredPostgres = !IsPlaceholder(_configuration.GetConnectionString("NetRatelDb") ?? _configuration.GetConnectionString("Default"));
        return _store.ClaimSetupAsync(
            proof,
            hasConfiguredPostgres ? "PostgreSQL" : null,
            hasConfiguredPostgres ? "ConnectionStrings:NetRatelDb" : null,
            cancellationToken);
    }

    public static BootstrapStatus ToStatus(BootstrapDescriptor descriptor) => new(
        descriptor.State,
        descriptor.State is BootstrapState.Unconfigured or BootstrapState.Configuring,
        descriptor.State == BootstrapState.Ready,
        descriptor.State == BootstrapState.RecoveryRequired,
        descriptor.SelectedProvider,
        descriptor.OperationId);

    private async Task<LegacyProbeResult> ProbeExistingInstallationAsync(string connectionString, CancellationToken cancellationToken)
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

        if (!tables.Contains("Tenants"))
        {
            return LegacyProbeResult.EmptyOrUnknown;
        }

        if (!await HasRowsAsync(connection, "Tenants", cancellationToken).ConfigureAwait(false))
        {
            return LegacyProbeResult.EmptyOrUnknown;
        }

        var continuityTables = new[] { "Agents", "AgentCredentials", "AgentRefreshTokens", "OidcSigningKeys", "OutboxMessages", "JobRuns", "Requests" };
        var hasContinuityEvidence = false;
        foreach (var table in continuityTables.Where(tables.Contains))
        {
            if (await HasRowsAsync(connection, table, cancellationToken).ConfigureAwait(false))
            {
                hasContinuityEvidence = true;
                break;
            }
        }

        return hasContinuityEvidence ? LegacyProbeResult.Adoptable : LegacyProbeResult.RequiresRecovery;
    }

    private static async Task<bool> HasRowsAsync(NpgsqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM \"{table}\")", connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

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

using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NuGet.Versioning;
using Npgsql;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NetRatel.API.Services;

public sealed record ClientUpdateOfferSnapshot(
    Guid ReleaseId,
    long CatalogRevision,
    string RuntimeId,
    string Version,
    string Sha256,
    long SizeBytes,
    string DownloadPath);

public sealed record ClientUpdatePolicySnapshot(
    long Revision,
    bool AutoUpdateEnabled,
    bool Suspended,
    bool ResumeRequested,
    Guid? SuppressedReleaseId);

public interface IClientUpdateCatalog
{
    ClientUpdateOfferSnapshot? GetOffer(int tenantId, Guid agentId, string runtimeId, string currentVersion, string channel);
    ClientUpdatePolicySnapshot GetPolicy(int tenantId, Guid agentId, long clientPolicyRevision);
    long Revision { get; }
    DateTimeOffset RefreshedAtUtc { get; }
    Task RefreshAsync(CancellationToken cancellationToken);
}

public sealed class ClientUpdateCatalog(IServiceScopeFactory scopes, TimeProvider timeProvider) : IClientUpdateCatalog
{
    private CatalogState _state = CatalogState.Empty;

    public long Revision => Volatile.Read(ref _state).Revision;
    public DateTimeOffset RefreshedAtUtc => Volatile.Read(ref _state).RefreshedAtUtc;

    public ClientUpdateOfferSnapshot? GetOffer(
        int tenantId,
        Guid agentId,
        string runtimeId,
        string currentVersion,
        string channel)
    {
        var state = Volatile.Read(ref _state);
        if (timeProvider.GetUtcNow() - state.RefreshedAtUtc > TimeSpan.FromSeconds(90) ||
            !state.TenantPolicies.TryGetValue(tenantId, out var tenantPolicy) || !tenantPolicy.Enabled ||
            state.AgentStates.TryGetValue(agentId, out var agentState) && agentState.SuspendedAtUtc.HasValue ||
            !NuGetVersion.TryParse(currentVersion, out var current))
        {
            return null;
        }

        var allowPrerelease = string.Equals(channel, "prerelease", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(tenantPolicy.Channel, "prerelease", StringComparison.OrdinalIgnoreCase);
        var releases = allowPrerelease ? state.LatestReleaseByRuntime : state.LatestStableReleaseByRuntime;
        releases.TryGetValue(runtimeId, out var candidate);
        if (!string.IsNullOrWhiteSpace(tenantPolicy.TargetVersion))
            candidate = state.ReleaseByRuntimeAndVersion.Values.SingleOrDefault(x =>
                string.Equals(x.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Version.ToNormalizedString(), tenantPolicy.TargetVersion, StringComparison.OrdinalIgnoreCase));
        if (candidate is not null &&
            (candidate.Version <= current || agentState?.SuppressedReleaseId == candidate.PublicId))
            candidate = null;

        return candidate is null
            ? null
            : new ClientUpdateOfferSnapshot(
                candidate.PublicId,
                state.Revision,
                candidate.RuntimeId,
                candidate.Version.ToNormalizedString(),
                candidate.Sha256,
                candidate.SizeBytes,
                $"/api/v1/client-artifacts/{Uri.EscapeDataString(candidate.RuntimeId)}/{Uri.EscapeDataString(candidate.Version.ToNormalizedString())}/raw-download");
    }

    public ClientUpdatePolicySnapshot GetPolicy(int tenantId, Guid agentId, long clientPolicyRevision)
    {
        var state = Volatile.Read(ref _state);
        state.AgentStates.TryGetValue(agentId, out var agentState);
        var policyRevision = agentState?.PolicyRevision ?? 0;
        return new ClientUpdatePolicySnapshot(
            policyRevision,
            state.TenantPolicies.TryGetValue(tenantId, out var tenantPolicy) && tenantPolicy.Enabled,
            agentState?.SuspendedAtUtc.HasValue == true,
            policyRevision > clientPolicyRevision && agentState?.SuspendedAtUtc is null,
            agentState?.SuppressedReleaseId);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var revision = await db.ClientUpdateCatalogRevisions.AsNoTracking()
            .Where(x => x.Id == 1)
            .Select(x => x.Revision)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var releases = await db.ClientUpdateReleases.AsNoTracking()
            .Where(x => x.Enabled)
            .Select(x => new { x.PublicId, x.RuntimeId, x.Version, x.Sha256, x.SizeBytes })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var tenantPolicies = await db.Tenants.AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var agentStates = await db.AgentClientUpdateStates.AsNoTracking()
            .Select(x => new CatalogAgentState(x.AgentId, x.SuspendedAtUtc, x.SuppressedReleaseId, x.PolicyRevision))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var parsedReleases = releases
            .Select(x => NuGetVersion.TryParse(x.Version, out var version)
                ? new CatalogRelease(x.PublicId, x.RuntimeId, version, x.Sha256, x.SizeBytes)
                : null)
            .Where(x => x is not null)
            .Cast<CatalogRelease>()
            .ToArray();
        var latestReleaseByRuntime = parsedReleases
            .GroupBy(x => x.RuntimeId, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(x => x.Key, x => x.MaxBy(y => y.Version)!, StringComparer.OrdinalIgnoreCase);
        var latestStableReleaseByRuntime = parsedReleases
            .Where(x => !x.Version.IsPrerelease)
            .GroupBy(x => x.RuntimeId, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(x => x.Key, x => x.MaxBy(y => y.Version)!, StringComparer.OrdinalIgnoreCase);
        var refreshedAtUtc = timeProvider.GetUtcNow();
        Volatile.Write(ref _state, new CatalogState(
            revision,
            refreshedAtUtc,
            latestReleaseByRuntime,
            latestStableReleaseByRuntime,
            tenantPolicies.ToImmutableDictionary(x => x.Id, x => new CatalogTenantPolicy(x.AutoUpdate, x.AutoUpdateChannel, x.AutoUpdateTargetVersion)),
            parsedReleases.ToImmutableDictionary(x => (x.RuntimeId, x.Version.ToNormalizedString())),
            agentStates.ToImmutableDictionary(x => x.AgentId)));
        ClientUpdateTelemetry.CatalogRefreshed(revision, refreshedAtUtc);
    }

    private sealed record CatalogRelease(Guid PublicId, string RuntimeId, NuGetVersion Version, string Sha256, long SizeBytes);
    private sealed record CatalogAgentState(Guid AgentId, DateTimeOffset? SuspendedAtUtc, Guid? SuppressedReleaseId, long PolicyRevision);
    private sealed record CatalogTenantPolicy(bool Enabled, string Channel, string? TargetVersion);
    private sealed record CatalogState(
        long Revision,
        DateTimeOffset RefreshedAtUtc,
        ImmutableDictionary<string, CatalogRelease> LatestReleaseByRuntime,
        ImmutableDictionary<string, CatalogRelease> LatestStableReleaseByRuntime,
        ImmutableDictionary<int, CatalogTenantPolicy> TenantPolicies,
        ImmutableDictionary<(string RuntimeId, string Version), CatalogRelease> ReleaseByRuntimeAndVersion,
        ImmutableDictionary<Guid, CatalogAgentState> AgentStates)
    {
        public static CatalogState Empty { get; } = new(
            0,
            DateTimeOffset.MinValue,
            ImmutableDictionary<string, CatalogRelease>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary<string, CatalogRelease>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary<int, CatalogTenantPolicy>.Empty,
            ImmutableDictionary<(string RuntimeId, string Version), CatalogRelease>.Empty,
            ImmutableDictionary<Guid, CatalogAgentState>.Empty);
    }
}

public sealed class ClientUpdateCatalogRefreshService(
    IClientUpdateCatalog catalog,
    IConfiguration configuration,
    ILogger<ClientUpdateCatalogRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshSafelyAsync(stoppingToken).ConfigureAwait(false);
        await Task.WhenAll(
            RunPollAsync(stoppingToken),
            RunListenerAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task RunPollAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RefreshSafelyAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunListenerAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("NetRatelDb")
            ?? configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                connection.Notification += (_, _) => _ = RefreshSafelyAsync(stoppingToken);
                await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
                await using (var command = new NpgsqlCommand("LISTEN netratel_client_updates", connection))
                    await command.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                while (!stoppingToken.IsCancellationRequested)
                    await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Client update PostgreSQL notification listener disconnected; polling remains active.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RefreshSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Client update catalog refresh stopped with application shutdown.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Client update catalog refresh failed; presence will omit stale update offers.");
        }
    }
}

public sealed class ClientUpdateCatalogHealthCheck(IClientUpdateCatalog catalog, TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var age = timeProvider.GetUtcNow() - catalog.RefreshedAtUtc;
        var data = new Dictionary<string, object>
        {
            ["revision"] = catalog.Revision,
            ["ageSeconds"] = age == TimeSpan.MaxValue ? long.MaxValue : (long)Math.Max(0, age.TotalSeconds)
        };
        return Task.FromResult(age <= TimeSpan.FromSeconds(90)
            ? HealthCheckResult.Healthy("Client update catalog is current.", data)
            : HealthCheckResult.Degraded("Client update catalog has not refreshed within 90 seconds.", data: data));
    }
}

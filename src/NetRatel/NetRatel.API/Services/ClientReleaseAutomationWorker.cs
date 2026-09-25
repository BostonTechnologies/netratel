using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NuGet.Versioning;

namespace NetRatel.API.Services;

public sealed class ClientReleaseAutomationWorker(
    IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<ClientReleaseAutomationWorker> logger) : BackgroundService
{
    private const string AutomaticActor = "client-release-automation";
    private readonly Guid _workerId = Guid.NewGuid();
    private static readonly TimeSpan LeaseLength = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        do
        {
            try
            {
                await RunCheckIfDueAsync(stoppingToken);
                await PublishImportedIfEligibleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Client release automation cycle failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunCheckIfDueAsync(CancellationToken stoppingToken)
    {
        var now = clock.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var settings = await db.ClientReleaseAutomationSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1 && x.NextCheckAtUtc <= now &&
                (x.LeaseUntilUtc == null || x.LeaseUntilUtc < now), stoppingToken);
        if (settings is null) return;
        DateTimeOffset? next = settings.CheckEveryHours == 0 ? null :
            now.AddHours(settings.CheckEveryHours).AddSeconds(RandomNumberGenerator.GetInt32(0, 301));
        var claimed = await db.ClientReleaseAutomationSettings
            .Where(x => x.Id == 1 && x.NextCheckAtUtc <= now &&
                x.Revision == settings.Revision &&
                (x.LeaseUntilUtc == null || x.LeaseUntilUtc < now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseOwner, _workerId)
                .SetProperty(x => x.LeaseUntilUtc, now + LeaseLength)
                .SetProperty(x => x.LastAttemptAtUtc, now)
                .SetProperty(x => x.NextCheckAtUtc, next)
                .SetProperty(x => x.Revision, x => x.Revision + 1), stoppingToken);
        if (claimed != 1) return;
        var claimedRevision = settings.Revision + 1;

        using var owned = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = RenewLeaseAsync(owned, stoppingToken);
        string? failure = null;
        try { await CheckCatalogueAsync(scope.ServiceProvider, owned.Token); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Automatic GitHub client release check failed");
            failure = exception.Message.Length <= 2048 ? exception.Message : exception.Message[..2048];
        }
        finally
        {
            owned.Cancel();
            try { await renewal; }
            catch (OperationCanceledException) when (owned.IsCancellationRequested)
            {
                logger.LogDebug("Client release automation lease renewal stopped after the check completed");
            }
        }
        var finished = clock.GetUtcNow();
        await db.ClientReleaseAutomationSettings.Where(x => x.Id == 1 && x.LeaseOwner == _workerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseOwner, (Guid?)null)
                .SetProperty(x => x.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.LastSuccessAtUtc, x => failure == null ? finished : x.LastSuccessAtUtc)
                .SetProperty(x => x.LastError, failure)
                .SetProperty(x => x.NextCheckAtUtc, x => failure != null &&
                    x.Revision == claimedRevision && x.CheckEveryHours > 0
                    ? finished.AddMinutes(15) : x.NextCheckAtUtc)
                .SetProperty(x => x.Revision, x => x.Revision + 1), stoppingToken);
    }

    private async Task RenewLeaseAsync(CancellationTokenSource owned, CancellationToken stoppingToken)
    {
        try
        {
            while (!owned.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), owned.Token);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var renewed = await db.ClientReleaseAutomationSettings
                    .Where(x => x.Id == 1 && x.LeaseOwner == _workerId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.LeaseUntilUtc, clock.GetUtcNow() + LeaseLength), stoppingToken);
                if (renewed != 1) { owned.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (owned.IsCancellationRequested || stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Client release automation lease renewal cancelled");
        }
    }

    private async Task CheckCatalogueAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<OrchestratorDbContext>();
        var catalog = services.GetRequiredService<IGitHubClientReleaseCatalog>();
        var releases = new List<GitHubClientRelease>();
        for (var pageNumber = 0; pageNumber < 30; pageNumber++)
        {
            var page = await catalog.ListAsync("all", pageNumber, pageNumber == 0, ct);
            if (page.Warning is not null || page.ScanLimitReached)
                throw new InvalidDataException(page.Warning ?? "GitHub release catalogue scan limit was reached.");
            releases.AddRange(page.Items);
            if (!page.HasMore) break;
            if (pageNumber == 29)
                throw new InvalidDataException("GitHub release catalogue exceeded the bounded automation scan.");
        }

        var imports = services.GetRequiredService<ClientReleaseImportService>();
        foreach (var prerelease in new[] { false, true })
        {
            var policy = await db.ClientReleaseAutomationSettings.AsNoTracking()
                .SingleAsync(x => x.Id == 1, ct);
            if (policy.CheckEveryHours == 0 ||
                prerelease && !policy.DownloadPrerelease ||
                !prerelease && !policy.DownloadStable) continue;
            var newest = Newest(releases, prerelease);
            if (newest is null) continue;
            if (newest.PublicationState != "verification required" ||
                newest.PublicationAsset is null || newest.ChecksumsAsset is null ||
                newest.ClientAssets.Count == 0)
                throw new InvalidDataException($"The newest {Channel(prerelease)} client release has incomplete publication evidence.");
            var candidateVersion = NuGetVersion.Parse(newest.Version);
            var runtimes = newest.ClientAssets.Select(asset => asset.RuntimeId).ToArray();
            var offeredVersions = await db.ClientUpdateReleases.AsNoTracking()
                .Where(release => release.Enabled && runtimes.Contains(release.RuntimeId))
                .Select(release => new { release.RuntimeId, release.Channel, release.Version }).ToListAsync(ct);
            if (AllRuntimesCovered(runtimes,
                    offeredVersions.Select(value => (value.RuntimeId, value.Channel, value.Version)),
                    Channel(prerelease), candidateVersion)) continue;
            var existing = await db.ClientReleaseImportOperations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.GitHubReleaseId == newest.Id, ct);
            if (existing is null)
            {
                var queued = await imports.QueueAsync(newest.Id, AutomaticActor, ct);
                if (queued is not null)
                    await db.ClientReleaseImportOperations
                        .Where(x => x.Id == queued.Id && x.RequestedBy == AutomaticActor)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsAutomatic, true), ct);
            }
            else if (existing.IsAutomatic && existing.State is
                ClientReleaseImportState.Failed or ClientReleaseImportState.Cancelled)
                await imports.RetryAsync(existing.Id, ct);
        }
    }

    internal static GitHubClientRelease? Newest(IEnumerable<GitHubClientRelease> releases, bool prerelease)
    {
        GitHubClientRelease? best = null;
        NuGetVersion? bestVersion = null;
        foreach (var release in releases)
        {
            if (!NuGetVersion.TryParse(release.Version, out var version) ||
                version.ToNormalizedString() != release.Version ||
                version.IsPrerelease != release.IsPrerelease)
                throw new InvalidDataException("GitHub release version and prerelease metadata disagree.");
            if (release.IsPrerelease != prerelease) continue;
            if (bestVersion is null || version > bestVersion)
            {
                best = release;
                bestVersion = version;
            }
        }
        return best;
    }

    private static string Channel(bool prerelease) => prerelease ? "prerelease" : "stable";

    internal static bool AllRuntimesCovered(IEnumerable<string> runtimes,
        IEnumerable<(string RuntimeId, string Channel, string Version)> offered,
        string channel, NuGetVersion candidateVersion)
    {
        var eligible = offered.Where(value => string.Equals(value.Channel, channel, StringComparison.OrdinalIgnoreCase))
            .Select(value => (value.RuntimeId, Parsed: NuGetVersion.TryParse(value.Version, out var version) ? version : null))
            .Where(value => value.Parsed is not null).ToArray();
        return runtimes.All(runtimeId => eligible.Any(value =>
            string.Equals(value.RuntimeId, runtimeId, StringComparison.Ordinal) &&
            value.Parsed! >= candidateVersion));
    }

    internal async Task PublishImportedIfEligibleAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<OrchestratorDbContext>();
        var policy = await db.ClientReleaseAutomationSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (policy is null || policy.CheckEveryHours == 0 || !policy.PublishAutomatically) return;
        var now = clock.GetUtcNow();
        var threshold = now.AddHours(-1);
        var candidates = await db.ClientReleaseImportOperations.AsNoTracking()
            .Where(x => x.IsAutomatic && x.State == ClientReleaseImportState.Imported &&
                x.PublishedAtUtc == null &&
                (x.AutomaticPublishAttemptAtUtc == null || x.AutomaticPublishAttemptAtUtc < threshold))
            .OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);

        ClientReleaseImportOperation? operation = null;
        NuGetVersion? version = null;
        foreach (var candidate in candidates)
        {
            if (!NuGetVersion.TryParse(candidate.Version, out var parsed) ||
                !string.Equals(parsed.ToNormalizedString(), candidate.Version, StringComparison.Ordinal))
            {
                logger.LogWarning("Skipping automatic publication candidate {OperationId} with invalid version {Version}.",
                    candidate.Id, candidate.Version);
                continue;
            }

            if (parsed.IsPrerelease && (!policy.DownloadPrerelease || !policy.DeployPrereleaseAutomatically) ||
                !parsed.IsPrerelease && !policy.DownloadStable)
            {
                logger.LogDebug("Skipping automatic publication candidate {OperationId} because its {Channel} channel is not eligible under current policy.",
                    candidate.Id, parsed.IsPrerelease ? "prerelease" : "stable");
                continue;
            }

            operation = candidate;
            version = parsed;
            break;
        }

        if (operation is null || version is null) return;
        var claimed = await db.ClientReleaseImportOperations
            .Where(x => x.Id == operation.Id && x.PublishedAtUtc == null &&
                (x.AutomaticPublishAttemptAtUtc == null || x.AutomaticPublishAttemptAtUtc < threshold))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AutomaticPublishAttemptAtUtc, now)
                .SetProperty(x => x.AutomaticPublishError, (string?)null), ct);
        if (claimed != 1) return;
        try
        {
            await services.GetRequiredService<ClientReleaseImportService>()
                .PublishAsync(operation.Id, AutomaticActor, version.IsPrerelease, ct, automatic: true);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Automatic client release publication failed for {OperationId}", operation.Id);
            var detail = exception is InvalidOperationException or InvalidDataException or ClientArtifactConflictException
                ? exception.Message : "Automatic publication failed; inspect server diagnostics.";
            var message = detail.Length <= 2048 ? detail : detail[..2048];
            await db.ClientReleaseImportOperations.Where(x => x.Id == operation.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AutomaticPublishError, message), ct);
        }
    }
}

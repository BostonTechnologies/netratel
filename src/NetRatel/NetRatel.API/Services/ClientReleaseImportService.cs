using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services;

public sealed record ClientReleaseImportClaim(Guid OperationId, Guid LeaseOwner, long LeaseGeneration);

public sealed record ClientReleaseImportStatus(
    Guid Id, long GitHubReleaseId, string Tag, string Version, ClientReleaseImportState State,
    long? TotalBytes, long DownloadedBytes, string? Error, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, DateTimeOffset? PublishedAtUtc,
    IReadOnlyList<ClientReleaseImportAssetStatus> Assets, string? AutomaticPublishError = null);

public sealed record ClientReleaseImportAssetStatus(
    string RuntimeId, string SourceName, ClientReleaseImportAssetState State,
    long SourceSizeBytes, long DownloadedBytes, string? SourceSha256, string? LocalSha256, string? Error);

public sealed class ClientReleaseImportService(
    OrchestratorDbContext db,
    IGitHubClientReleaseCatalog catalog,
    IClientArtifactsService artifacts,
    ClientUpdateAuthorityService updates,
    TimeProvider clock)
{
    public async Task<ClientReleaseImportStatus?> QueueAsync(long releaseId, string requestedBy, CancellationToken ct)
    {
        if (releaseId <= 0) return null;
        var release = await catalog.FindAsync(releaseId, ct);
        if (release is null) return null;
        var existing = await db.ClientReleaseImportOperations.Include(x => x.Assets)
            .SingleOrDefaultAsync(x => x.GitHubReleaseId == releaseId, ct);
        if (existing is not null) return ToStatus(existing);
        var now = clock.GetUtcNow();
        var operation = new ClientReleaseImportOperation
        {
            Id = Guid.NewGuid(), GitHubReleaseId = releaseId, Tag = release.Tag,
            Version = release.Version, RequestedBy = requestedBy,
            State = ClientReleaseImportState.Queued, CreatedAtUtc = now, UpdatedAtUtc = now,
            TotalBytes = release.TotalClientBytes
        };
        db.ClientReleaseImportOperations.Add(operation);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(operation).State = EntityState.Detached;
            existing = await db.ClientReleaseImportOperations.Include(x => x.Assets)
                .SingleOrDefaultAsync(x => x.GitHubReleaseId == releaseId, ct);
            if (existing is null) throw;
            return ToStatus(existing);
        }
        return ToStatus(operation);
    }

    public async Task<ClientReleaseImportStatus?> GetAsync(Guid id, CancellationToken ct)
    {
        var operation = await db.ClientReleaseImportOperations.AsNoTracking().Include(x => x.Assets)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        return operation is null ? null : ToStatus(operation);
    }

    public async Task<IReadOnlyList<ClientReleaseImportStatus>> ListAsync(CancellationToken ct) =>
        (await db.ClientReleaseImportOperations.AsNoTracking().Include(x => x.Assets)
            .OrderByDescending(x => x.CreatedAtUtc).Take(100).ToListAsync(ct))
        .Select(ToStatus).ToArray();

    public async Task<bool> CancelAsync(Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.ClientReleaseImportOperations
            .Where(x => x.Id == id && x.State != ClientReleaseImportState.Imported &&
                x.State != ClientReleaseImportState.Cancelled)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CancellationRequested, true)
                .SetProperty(x => x.UpdatedAtUtc, now), ct) > 0;
    }

    public async Task<bool> RetryAsync(Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.ClientReleaseImportOperations
            .Where(x => x.Id == id && (x.State == ClientReleaseImportState.Failed ||
                x.State == ClientReleaseImportState.Cancelled))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, ClientReleaseImportState.Queued)
                .SetProperty(x => x.CancellationRequested, false)
                .SetProperty(x => x.Error, (string?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), ct) > 0;
    }

    public async Task<bool> PublishAsync(Guid id, string publishedBy, bool confirmPrerelease, CancellationToken ct,
        bool automatic = false)
    {
        var operation = await db.ClientReleaseImportOperations.AsNoTracking().Include(x => x.Assets)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (operation is null) return false;
        if (operation.State != ClientReleaseImportState.Imported || operation.BuildCommit is null ||
            operation.Assets.Count == 0 || operation.Assets.Any(x => x.State != ClientReleaseImportAssetState.Imported))
            throw new InvalidOperationException("The selected client pack has not completed import.");
        var items = new List<ClientPackPublishItem>(operation.Assets.Count);
        foreach (var asset in operation.Assets)
        {
            var metadata = await artifacts.GetMetadataAsync(asset.RuntimeId, operation.Version, ct)
                ?? throw new InvalidOperationException("An imported runtime archive is unavailable.");
            if (metadata.Sha256 != asset.LocalSha256 || metadata.Size != asset.LocalSizeBytes)
                throw new ClientArtifactConflictException(asset.RuntimeId, operation.Version);
            var download = await artifacts.DownloadRawAsync(asset.RuntimeId, operation.Version, ct);
            await using (download.Content)
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(download.Content, ct)).ToLowerInvariant();
                if (actual != asset.LocalSha256)
                    throw new ClientArtifactConflictException(asset.RuntimeId, operation.Version);
            }
            var manifestJson = await ClientArtifactManifestValidator.ValidateAsync(artifacts, metadata, ct);
            var manifest = JsonSerializer.Deserialize<ClientArtifactManifest>(manifestJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (manifest is null || !string.Equals(manifest.CommitSha, operation.BuildCommit,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The imported build manifest differs from the verified source commit.");
            items.Add(new ClientPackPublishItem(metadata, manifestJson));
        }
        await updates.PublishImportedPackAsync(id, items, publishedBy, confirmPrerelease, ct, automatic);
        return true;
    }

    private static ClientReleaseImportStatus ToStatus(ClientReleaseImportOperation operation) =>
        new(operation.Id, operation.GitHubReleaseId, operation.Tag, operation.Version,
            operation.State, operation.TotalBytes,
            Math.Max(operation.DownloadedBytes, operation.Assets.Sum(x => x.DownloadedBytes)), operation.Error,
            operation.CreatedAtUtc, operation.UpdatedAtUtc, operation.PublishedAtUtc,
            operation.Assets.OrderBy(x => x.RuntimeId, StringComparer.Ordinal)
                .Select(x => new ClientReleaseImportAssetStatus(x.RuntimeId, x.SourceName,
                    x.State, x.SourceSizeBytes, x.DownloadedBytes, x.SourceSha256,
                    x.LocalSha256, x.Error)).ToArray(), operation.AutomaticPublishError);
}

public sealed class ClientReleaseImportWorker(
    IServiceScopeFactory scopes,
    IOptions<ClientArtifactsOptions> options,
    IWebHostEnvironment environment,
    TimeProvider clock,
    ILogger<ClientReleaseImportWorker> logger) : BackgroundService
{
    private readonly Guid _workerId = Guid.NewGuid();
    private static readonly TimeSpan LeaseLength = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var visibilityRepaired = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!visibilityRepaired)
                {
                    await RepairCompletedVisibilityAsync(stoppingToken);
                    visibilityRepaired = true;
                }
                var claim = await ClaimNextAsync(stoppingToken);
                if (claim is not null) await ProcessAsync(claim, stoppingToken);
                else await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Client release importer cycle failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task RepairCompletedVisibilityAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<OrchestratorDbContext>();
        var artifacts = services.GetRequiredService<IClientArtifactsService>();
        var ids = await db.ClientReleaseImportOperations.AsNoTracking()
            .Where(x => x.State == ClientReleaseImportState.Imported)
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var id in ids) await artifacts.CompleteImportVisibilityAsync(id, ct);
    }

    private async Task<ClientReleaseImportClaim?> ClaimNextAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = clock.GetUtcNow();
        var ids = await db.ClientReleaseImportOperations.AsNoTracking()
            .Where(x => x.State == ClientReleaseImportState.Queued ||
                x.State >= ClientReleaseImportState.Resolving && x.State <= ClientReleaseImportState.Importing &&
                x.LeaseUntilUtc < now)
            .OrderBy(x => x.CreatedAtUtc).Select(x => x.Id).Take(10).ToListAsync(ct);
        foreach (var id in ids)
        {
            var updated = await db.ClientReleaseImportOperations
                .Where(x => x.Id == id && (x.State == ClientReleaseImportState.Queued ||
                    x.State >= ClientReleaseImportState.Resolving && x.State <= ClientReleaseImportState.Importing &&
                    x.LeaseUntilUtc < now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.LeaseOwner, _workerId)
                    .SetProperty(x => x.LeaseUntilUtc, now + LeaseLength)
                    .SetProperty(x => x.LeaseGeneration, x => x.LeaseGeneration + 1)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.State, ClientReleaseImportState.Resolving)
                    .SetProperty(x => x.UpdatedAtUtc, now), ct);
            if (updated == 1)
            {
                var generation = await db.ClientReleaseImportOperations.AsNoTracking()
                    .Where(x => x.Id == id && x.LeaseOwner == _workerId)
                    .Select(x => x.LeaseGeneration).SingleAsync(ct);
                return new ClientReleaseImportClaim(id, _workerId, generation);
            }
        }
        return null;
    }

    private async Task ProcessAsync(ClientReleaseImportClaim claim, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<OrchestratorDbContext>();
        var catalog = services.GetRequiredService<IGitHubClientReleaseCatalog>();
        var downloader = services.GetRequiredService<GitHubClientAssetDownloader>();
        var artifacts = services.GetRequiredService<IClientArtifactsService>();
        var operation = await db.ClientReleaseImportOperations.Include(x => x.Assets)
            .SingleOrDefaultAsync(x => x.Id == claim.OperationId && x.LeaseOwner == claim.LeaseOwner &&
                x.LeaseGeneration == claim.LeaseGeneration, stoppingToken);
        if (operation is null) return;
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = RenewLeaseAsync(claim, owned, stoppingToken);
        var root = options.Value.StorageRoot;
        if (!Path.IsPathRooted(root)) root = Path.Combine(environment.ContentRootPath, root);
        var work = Path.Combine(root, ".import-work", claim.OperationId.ToString("N"),
            $"{claim.LeaseOwner:N}-{claim.LeaseGeneration}");
        Directory.CreateDirectory(work);
        try
        {
            await CopyPreviousClaimFilesAsync(root, claim, work, stoppingToken);
            var ct = owned.Token;
            await CheckCancellationAsync(db, claim, ct);
            var release = await catalog.FindAsync(operation.GitHubReleaseId, ct)
                ?? throw new InvalidDataException("The selected GitHub release is no longer available.");
            if (release.Tag != operation.Tag || release.Version != operation.Version ||
                release.PublicationAsset is null || release.ChecksumsAsset is null)
                throw new InvalidDataException("The selected release identity or publication evidence changed.");
            var commit = await catalog.ResolveTagCommitAsync(release.Tag, ct);
            if (operation.BuildCommit is not null && operation.BuildCommit != commit)
                throw new InvalidDataException("The release tag now resolves to a different commit.");
            operation.BuildCommit = commit;
            await SetStateAsync(db, operation, claim, ClientReleaseImportState.Downloading, ct);
            var publication = await DownloadEvidenceAsync(downloader, release.PublicationAsset,
                Path.Combine(work, "publication.json"), ct);
            var checksums = await DownloadEvidenceAsync(downloader, release.ChecksumsAsset,
                Path.Combine(work, "SHA256SUMS"), ct);
            if (operation.PublicationSha256 is not null && operation.PublicationSha256 != publication.Sha256)
                throw new InvalidDataException("The release publication record changed during retry.");
            operation.PublicationSha256 = publication.Sha256;
            await SetStateAsync(db, operation, claim, ClientReleaseImportState.Verifying, ct);
            var verified = await ClientReleasePublicationVerifier.VerifyAsync(release,
                Path.Combine(work, "publication.json"), Path.Combine(work, "SHA256SUMS"), commit, ct);
            operation.TotalBytes = verified.Assets.Sum(x => x.SizeBytes);
            foreach (var source in verified.Assets)
            {
                var existing = operation.Assets.SingleOrDefault(x => x.RuntimeId == source.RuntimeId);
                if (existing is null)
                {
                    existing = new ClientReleaseImportAsset
                    {
                        OperationId = claim.OperationId, RuntimeId = source.RuntimeId, GitHubAssetId = source.AssetId,
                        SourceName = source.Name, SourceSha256 = source.SourceSha256,
                        SourceSizeBytes = source.SizeBytes, UpdatedAtUtc = clock.GetUtcNow()
                    };
                    operation.Assets.Add(existing);
                }
                else if (existing.GitHubAssetId != source.AssetId || existing.SourceName != source.Name ||
                    existing.SourceSha256 != source.SourceSha256 || existing.SourceSizeBytes != source.SizeBytes)
                    throw new InvalidDataException("The client release asset identity changed during retry.");
            }
            if (operation.Assets.Count != verified.Assets.Count)
                throw new InvalidDataException("The client pack inventory changed during retry.");
            await CheckCancellationAsync(db, claim, ct);
            await SaveClaimedMutationAsync(db, claim, ct);

            foreach (var source in verified.Assets)
            {
                await CheckCancellationAsync(db, claim, ct);
                var asset = operation.Assets.Single(x => x.RuntimeId == source.RuntimeId);
                var sourcePath = Path.Combine(work, source.Name);
                if (File.Exists(sourcePath))
                {
                    try { await ClientReleasePublicationVerifier.VerifyDownloadedAssetAsync(sourcePath, source, ct); }
                    catch (InvalidDataException) { File.Delete(sourcePath); }
                }
                if (!File.Exists(sourcePath))
                {
                    asset.State = ClientReleaseImportAssetState.Downloading;
                    asset.DownloadedBytes = 0;
                    operation.DownloadedBytes = operation.Assets.Sum(x => x.DownloadedBytes);
                    await SetStateAsync(db, operation, claim, ClientReleaseImportState.Downloading, ct);
                    var result = await downloader.DownloadAsync(source.AssetId, source.SizeBytes,
                        "sha256:" + source.SourceSha256, sourcePath, ct,
                        (bytes, token) => ReportProgressAsync(claim, source.RuntimeId, bytes, token));
                    if (result.Sha256 != source.SourceSha256)
                        throw new InvalidDataException("Client archive source hash differs from publication.");
                }
                asset.State = ClientReleaseImportAssetState.Verified;
                asset.DownloadedBytes = source.SizeBytes;
                operation.DownloadedBytes = operation.Assets.Sum(x => x.DownloadedBytes);
                await SetStateAsync(db, operation, claim, ClientReleaseImportState.Verifying, ct);
                var localPath = Path.Combine(work, source.RuntimeId + ".zip");
                if (File.Exists(localPath))
                {
                    if (asset.LocalSha256 is null) File.Delete(localPath);
                    else
                    {
                        await using var input = File.OpenRead(localPath);
                        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
                        if (actual != asset.LocalSha256) File.Delete(localPath);
                    }
                }
                if (!File.Exists(localPath))
                {
                    var adapted = await ClientReleaseArchiveAdapter.NormalizeAsync(sourcePath, localPath,
                        source.RuntimeId, verified.Version, verified.BuildCommit, ct);
                    asset.LocalSha256 = adapted.Sha256;
                    asset.LocalSizeBytes = adapted.SizeBytes;
                    asset.ConversionContract = adapted.Contract;
                }
                asset.State = ClientReleaseImportAssetState.Normalized;
                asset.UpdatedAtUtc = clock.GetUtcNow();
                operation.UpdatedAtUtc = asset.UpdatedAtUtc;
                await CheckCancellationAsync(db, claim, ct);
                await SaveClaimedMutationAsync(db, claim, ct);
            }

            await CheckCancellationAsync(db, claim, ct);
            await SetStateAsync(db, operation, claim, ClientReleaseImportState.Importing, ct);
            foreach (var source in verified.Assets)
            {
                var asset = operation.Assets.Single(x => x.RuntimeId == source.RuntimeId);
                var localPath = Path.Combine(work, source.RuntimeId + ".zip");
                await using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous);
                var file = new FormFile(input, 0, input.Length, "file", Path.GetFileName(localPath))
                { Headers = new HeaderDictionary(), ContentType = "application/zip" };
                await artifacts.ImportVerifiedAsync(file, source.RuntimeId, verified.Version,
                    new ClientArtifactImportProvenance(claim.OperationId, verified.Repository, verified.Tag,
                        source.AssetId, source.Name, source.SourceSha256, verified.BuildCommit,
                        asset.ConversionContract ?? ClientReleaseArchiveAdapter.Contract,
                        claim.LeaseOwner, claim.LeaseGeneration),
                    operation.RequestedBy, ct);
                asset.State = ClientReleaseImportAssetState.Imported;
                asset.UpdatedAtUtc = clock.GetUtcNow();
                operation.UpdatedAtUtc = asset.UpdatedAtUtc;
                await CheckCancellationAsync(db, claim, ct);
                await SaveClaimedMutationAsync(db, claim, ct);
            }
            // The terminal state is the browser's signal that every runtime can be
            // downloaded. Make the pack visible first so an observer cannot see an
            // imported operation while the visibility marker is still unwritten.
            await CheckCancellationAsync(db, claim, ct);
            await artifacts.CompleteImportVisibilityAsync(claim.OperationId, claim, stoppingToken);
            operation.State = ClientReleaseImportState.Imported;
            operation.ImportedAtUtc = clock.GetUtcNow();
            operation.UpdatedAtUtc = operation.ImportedAtUtc.Value;
            operation.LeaseOwner = null;
            operation.LeaseUntilUtc = null;
            await db.SaveChangesAsync(ct);
            Directory.Delete(work, recursive: true);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The lease expires after restart. Verified staged bytes remain available for resume.
            logger.LogInformation("Client release import {OperationId} will resume after service restart.", claim.OperationId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Client release import {OperationId} stopped", claim.OperationId);
            db.ChangeTracker.Clear();
            var current = await db.ClientReleaseImportOperations.SingleAsync(x => x.Id == claim.OperationId, stoppingToken);
            if (current.LeaseOwner == claim.LeaseOwner && current.LeaseGeneration == claim.LeaseGeneration)
            {
                current.State = current.CancellationRequested ? ClientReleaseImportState.Cancelled : ClientReleaseImportState.Failed;
                current.Error = exception is OperationCanceledException ? "Import cancelled." : exception.Message[..Math.Min(exception.Message.Length, 2000)];
                current.UpdatedAtUtc = clock.GetUtcNow();
                current.LeaseOwner = null;
                current.LeaseUntilUtc = null;
                await db.SaveChangesAsync(stoppingToken);
            }
        }
        finally
        {
            owned.Cancel();
            try { await renewal; }
            catch (OperationCanceledException)
            {
                logger.LogDebug("Lease renewal stopped for client release import {OperationId}.", claim.OperationId);
            }
        }
    }

    private async Task RenewLeaseAsync(ClientReleaseImportClaim claim, CancellationTokenSource owned,
        CancellationToken stoppingToken)
    {
        while (!owned.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), owned.Token);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var now = clock.GetUtcNow();
            var current = await db.ClientReleaseImportOperations.AsNoTracking()
                .Where(x => x.Id == claim.OperationId && x.LeaseOwner == claim.LeaseOwner &&
                    x.LeaseGeneration == claim.LeaseGeneration && x.LeaseUntilUtc > now)
                .Select(x => new { x.CancellationRequested, x.State }).SingleOrDefaultAsync(stoppingToken);
            if (current is null || current.CancellationRequested) { owned.Cancel(); return; }
            var updated = await db.ClientReleaseImportOperations
                .Where(x => x.Id == claim.OperationId && x.LeaseOwner == claim.LeaseOwner &&
                    x.LeaseGeneration == claim.LeaseGeneration &&
                    x.LeaseUntilUtc > now &&
                    x.State >= ClientReleaseImportState.Resolving && x.State <= ClientReleaseImportState.Importing)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LeaseUntilUtc,
                    now + LeaseLength), stoppingToken);
            if (updated != 1) { owned.Cancel(); return; }
        }
    }

    private static async Task<DownloadedGitHubAsset> DownloadEvidenceAsync(
        GitHubClientAssetDownloader downloader, GitHubReleaseEvidenceAsset asset, string path, CancellationToken ct)
    {
        if (File.Exists(path)) File.Delete(path);
        return await downloader.DownloadAsync(asset.Id, asset.SizeBytes, asset.Sha256Digest, path, ct);
    }

    private static Task CopyPreviousClaimFilesAsync(string root, ClientReleaseImportClaim claim, string work,
        CancellationToken ct)
    {
        var operationRoot = Path.Combine(root, ".import-work", claim.OperationId.ToString("N"));
        if (!Directory.Exists(operationRoot)) return Task.CompletedTask;

        foreach (var previous in Directory.EnumerateDirectories(operationRoot))
        {
            if (string.Equals(previous, work, StringComparison.Ordinal)) continue;
            foreach (var source in Directory.EnumerateFiles(previous, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(previous, source);
                var destination = Path.Combine(work, relative);
                if (File.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                try { File.Copy(source, destination); }
                catch (IOException) when (File.Exists(destination))
                {
                    ct.ThrowIfCancellationRequested();
                }
            }
        }
        return Task.CompletedTask;
    }

    private async ValueTask ReportProgressAsync(ClientReleaseImportClaim claim, string runtimeId, long bytes,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var updated = await db.ClientReleaseImportAssets
            .Where(x => x.OperationId == claim.OperationId && x.RuntimeId == runtimeId &&
                x.Operation.LeaseOwner == claim.LeaseOwner &&
                x.Operation.LeaseGeneration == claim.LeaseGeneration &&
                x.Operation.LeaseUntilUtc > clock.GetUtcNow() &&
                x.State == ClientReleaseImportAssetState.Downloading)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.DownloadedBytes, bytes)
                .SetProperty(x => x.UpdatedAtUtc, clock.GetUtcNow()), ct);
        if (updated != 1)
            throw new InvalidOperationException("Client release import lease was lost while reporting progress.");
    }

    private async Task SetStateAsync(OrchestratorDbContext db, ClientReleaseImportOperation operation,
        ClientReleaseImportClaim claim, ClientReleaseImportState state, CancellationToken ct)
    {
        await CheckCancellationAsync(db, claim, ct);
        operation.State = state;
        operation.UpdatedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private async Task SaveClaimedMutationAsync(OrchestratorDbContext db,
        ClientReleaseImportClaim claim, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var fenced = await db.ClientReleaseImportOperations
            .Where(x => x.Id == claim.OperationId &&
                x.LeaseOwner == claim.LeaseOwner &&
                x.LeaseGeneration == claim.LeaseGeneration &&
                x.LeaseUntilUtc > now &&
                !x.CancellationRequested &&
                x.State >= ClientReleaseImportState.Resolving &&
                x.State <= ClientReleaseImportState.Importing)
            // The self-assignment is intentional. ExecuteUpdate emits a real
            // parent UPDATE and obtains its row lock even when the pending
            // mutation contains only dependent asset changes.
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.UpdatedAtUtc, x => x.UpdatedAtUtc), ct);
        if (fenced != 1)
            throw new InvalidOperationException("Client release import lease was lost before persisting asset changes.");

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task CheckCancellationAsync(OrchestratorDbContext db,
        ClientReleaseImportClaim claim, CancellationToken ct)
    {
        var current = await db.ClientReleaseImportOperations.AsNoTracking()
            .Where(x => x.Id == claim.OperationId && x.LeaseOwner == claim.LeaseOwner &&
                x.LeaseGeneration == claim.LeaseGeneration)
            .Select(x => new { x.CancellationRequested, x.LeaseUntilUtc })
            .SingleOrDefaultAsync(ct);
        if (current is null || current.LeaseUntilUtc <= clock.GetUtcNow())
            throw new InvalidOperationException("Client release import lease was lost.");
        if (current.CancellationRequested) throw new OperationCanceledException("Client release import was cancelled.");
    }
}

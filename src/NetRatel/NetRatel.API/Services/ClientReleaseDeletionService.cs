using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Persistence;
using NuGet.Versioning;

namespace NetRatel.API.Services;

public sealed record ClientReleaseDeletionPlan(string Version, string[] RuntimeIds, Guid[] ImportIds,
    Guid[] ReleaseIds, long ReclaimableBytes, string[] Blockers, int HistoricalAttempts, Guid[]? UploadStagingIds = null);
public sealed record ClientReleaseDeletionStatus(Guid Id, string Version, string State, bool Suppressed,
    DateTimeOffset UpdatedAtUtc, string? Error);

/// <summary>
/// A journal is committed before any filesystem mutation. Suppression fences new users of the
/// package; retries repeat missing-file-safe cleanup before removing metadata and live FKs.
/// </summary>
public sealed class ClientReleaseDeletionService(OrchestratorDbContext db,
    IOptions<ClientArtifactsOptions> options, IWebHostEnvironment environment, TimeProvider clock)
{
    private static readonly HashSet<string> Runtimes = ["win-x64", "win-arm64", "linux-x64", "osx-x64", "osx-arm64"];
    private string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.IsPathRooted(options.Value.StorageRoot)
        ? options.Value.StorageRoot : Path.Combine(environment.ContentRootPath, options.Value.StorageRoot)));

    public static Task<bool> IsSuppressedAsync(OrchestratorDbContext context, string version, CancellationToken ct) =>
        context.ClientReleaseDeletionReceipts.AsNoTracking().AnyAsync(x => x.Version == version && x.Suppressed, ct);

    // Serialize admission to new work with the mutation-time dependency inventory. The lock is
    // deliberately short: cleanup happens after the durable fence commits.
    public static async Task LockAdmissionsAsync(OrchestratorDbContext context, CancellationToken ct)
    {
        if (context.Database.IsNpgsql())
            await context.Database.ExecuteSqlRawAsync("LOCK TABLE \"ClientReleaseDeletionReceipts\" IN SHARE ROW EXCLUSIVE MODE", ct);
    }

    public async Task<ClientReleaseDeletionPlan> PreviewAsync(string version, CancellationToken ct)
    {
        ValidateVersion(version);
        var releases = await db.ClientUpdateReleases.AsNoTracking().Where(x => x.Version == version).ToListAsync(ct);
        var imports = await db.ClientReleaseImportOperations.AsNoTracking().Include(x => x.Assets)
            .Where(x => x.Version == version).ToListAsync(ct);
        var uploadBlockers = new List<string>();
        var uploads = new List<(Guid Id, string RuntimeId)>();
        try { uploads = await OwnedUploadStagingAsync(version, ct); }
        catch (IOException exception) { uploadBlockers.Add(exception.Message); }
        var runtimes = releases.Select(x => x.RuntimeId).Concat(imports.SelectMany(x => x.Assets).Select(x => x.RuntimeId))
            .Concat(uploads.Select(x => x.RuntimeId))
            .Concat(Runtimes.Where(r => Directory.Exists(PackagePath(r, version)))).Distinct().Order().ToArray();
        foreach (var runtime in runtimes) ValidateRuntime(runtime);
        var paths = Paths(version, runtimes, imports.Select(x => x.Id).ToArray(), uploads.Select(x => x.Id).ToArray()).ToArray();
        var blockers = await BlockersAsync(version, releases, imports, runtimes, ct);
        blockers.AddRange(uploadBlockers);
        long bytes = 0;
        foreach (var path in paths)
        {
            try { ValidateOwnedTree(path); bytes += Size(path); }
            catch (IOException exception) { blockers.Add(exception.Message); }
        }
        var history = await db.ClientUpdateAttempts.CountAsync(x => x.TargetVersion == version, ct);
        return new(version, runtimes, imports.Select(x => x.Id).ToArray(), releases.Select(x => x.PublicId).ToArray(),
            bytes, blockers.Distinct().ToArray(), history, uploads.Select(x => x.Id).ToArray());
    }

    public async Task<ClientReleaseDeletionStatus> DeleteAsync(string version, string requestedBy, CancellationToken ct)
    {
        ValidateVersion(version);
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAdmissionsAsync(db, ct);
            // All package consumers lock the admission fence before persisting work. PostgreSQL
            // dependency table locks also cover concurrent tenant-policy edits and old writers.
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"ClientUpdateReleases\", \"ClientUpdateAttempts\", \"ClientReleaseImportOperations\", \"ClientInstallGrants\", \"Tenants\", \"ClientArtifactDownloadLeases\" IN SHARE ROW EXCLUSIVE MODE", ct);
            var receipt = await db.ClientReleaseDeletionReceipts.SingleOrDefaultAsync(x => x.Version == version, ct);
            if (receipt?.State == "Deleted" && receipt.Suppressed) return Status(receipt);
            if (receipt is null || !receipt.Suppressed)
            {
                var plan = await PreviewAsync(version, ct);
                if (plan.Blockers.Length != 0) throw new InvalidOperationException(string.Join(" ", plan.Blockers));
                if (plan.RuntimeIds.Length == 0 && plan.ImportIds.Length == 0)
                    throw new KeyNotFoundException("No local release, package or import exists for this version.");
                if (receipt is not null)
                {
                    var previous = JsonSerializer.Deserialize<List<JsonElement>>(receipt.PreviousReceiptsJson) ?? [];
                    previous.Add(JsonSerializer.SerializeToElement(new { receipt.RequestedBy, receipt.CreatedAtUtc, receipt.DeletedAtUtc, receipt.PlanJson }));
                    receipt.PreviousReceiptsJson = JsonSerializer.Serialize(previous);
                }
                receipt ??= new ClientReleaseDeletionReceipt { Id = Guid.NewGuid(), Version = version };
                if (db.Entry(receipt).State == EntityState.Detached) db.ClientReleaseDeletionReceipts.Add(receipt);
                receipt.PlanJson = JsonSerializer.Serialize(plan);
                receipt.CreatedAtUtc = clock.GetUtcNow();
                receipt.RequestedBy = requestedBy;
                receipt.DeletedAtUtc = null;
            }
            receipt.Suppressed = true;
            receipt.State = "Pending";
            receipt.Error = null;
            receipt.UpdatedAtUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        return await ResumeAsync(version, ct);
    }

    public async Task<ClientReleaseDeletionStatus> ResumeAsync(string version, CancellationToken ct)
    {
        var receipt = await db.ClientReleaseDeletionReceipts.SingleAsync(x => x.Version == version, ct);
        if (receipt.State == "Deleted") return Status(receipt);
        try
        {
            // Hold the fence during recovery too. The transaction rolls database changes back on
            // failure; the already committed journal retains the exact package ownership inventory.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await LockAdmissionsAsync(db, ct);
            await db.Entry(receipt).ReloadAsync(ct);
            if (receipt.State == "Deleted" || !receipt.Suppressed) return Status(receipt);
            var plan = JsonSerializer.Deserialize<ClientReleaseDeletionPlan>(receipt.PlanJson)
                ?? throw new InvalidDataException("The local deletion journal is invalid.");
            ValidateVersion(plan.Version);
            if (plan.Version != version) throw new InvalidDataException("The cleanup journal version differs from its receipt.");
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"ClientUpdateReleases\", \"ClientUpdateAttempts\", \"ClientReleaseImportOperations\", \"ClientInstallGrants\", \"Tenants\", \"ClientArtifactDownloadLeases\" IN SHARE ROW EXCLUSIVE MODE", ct);
            var releases = await db.ClientUpdateReleases.Where(x => x.Version == version).ToListAsync(ct);
            var imports = await db.ClientReleaseImportOperations.Include(x => x.Assets).Where(x => x.Version == version).ToListAsync(ct);
            var blockers = await BlockersAsync(version, releases, imports, plan.RuntimeIds, ct);
            if (blockers.Count != 0) throw new InvalidOperationException(string.Join(" ", blockers));
            foreach (var id in plan.UploadStagingIds ?? [])
            {
                var path = UploadStagingPath(id);
                if (Directory.Exists(path) && await UploadStagingRuntimeAsync(path, version, ct) is null)
                    throw new InvalidOperationException("Upload staging ownership changed; shared or unowned data cannot be purged.");
            }
            foreach (var path in Paths(version, plan.RuntimeIds, plan.ImportIds, plan.UploadStagingIds ?? []))
            {
                ValidateOwnedTree(path);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
                options.Value.TestDeletionHook?.Invoke();
            }
            foreach (var release in releases)
            {
                var attempts = await db.ClientUpdateAttempts.Where(x => x.ReleaseId == release.Id).ToListAsync(ct);
                foreach (var attempt in attempts)
                {
                    attempt.ReleaseRecordId = null;
                    attempt.PurgedReleasePublicId = release.PublicId;
                }
            }
            db.ClientUpdateReleases.RemoveRange(releases);
            db.ClientReleaseImportOperations.RemoveRange(imports);
            db.ClientArtifactDownloadLeases.RemoveRange(await db.ClientArtifactDownloadLeases
                .Where(x => x.Version == version).ToListAsync(ct));
            receipt.State = "Deleted";
            receipt.Error = null;
            receipt.UpdatedAtUtc = clock.GetUtcNow();
            receipt.DeletedAtUtc = receipt.UpdatedAtUtc;
            await db.SaveChangesAsync(ct);
            if (db.Database.IsNpgsql())
                await ClientUpdateActivityNotifications.NotifyAsync(db, null, ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or DbUpdateException or DbException or ArgumentException or JsonException)
        {
            db.ChangeTracker.Clear();
            await using var failureTransaction = await db.Database.BeginTransactionAsync(ct);
            await LockAdmissionsAsync(db, ct);
            receipt = await db.ClientReleaseDeletionReceipts.SingleAsync(x => x.Version == version, ct);
            if (!receipt.Suppressed || receipt.State == "Deleted") return Status(receipt);
            receipt.State = "Failed";
            receipt.UpdatedAtUtc = clock.GetUtcNow();
            receipt.Error = exception.Message.Length > 2048 ? exception.Message[..2048] : exception.Message;
            await db.SaveChangesAsync(ct);
            await failureTransaction.CommitAsync(ct);
        }
        return Status(receipt);
    }

    public async Task<IReadOnlyList<ClientReleaseDeletionStatus>> ListAsync(CancellationToken ct) =>
        (await db.ClientReleaseDeletionReceipts.AsNoTracking().OrderByDescending(x => x.UpdatedAtUtc).Take(100).ToListAsync(ct))
        .Select(Status).ToArray();

    private async Task<List<string>> BlockersAsync(string version, List<ClientUpdateReleaseRecord> releases,
        List<ClientReleaseImportOperation> imports, string[] runtimes, CancellationToken ct)
    {
        var blockers = new List<string>();
        var now = clock.GetUtcNow();
        if (releases.Any(x => x.Enabled)) blockers.Add("Disable every runtime release for this version before deletion.");
        if (imports.Any(x => x.State <= ClientReleaseImportState.Importing || x.LeaseUntilUtc > now))
            blockers.Add("Cancel active imports and wait for their leases to drain before deletion.");
        if (await db.ClientArtifactDownloadLeases.AnyAsync(x => x.Version == version && x.ExpiresAtUtc > now, ct))
            blockers.Add("An archive download lease or upload staging lease is active; wait for it to finish before deletion.");
        if (await db.ClientUpdateAttempts.AnyAsync(x => (x.TargetVersion == version || x.FromVersion == version) &&
            (x.State <= ClientUpdateAttemptState.GatewayReadmitted || x.State == ClientUpdateAttemptState.RollbackUnverified), ct))
            blockers.Add("An update or unverified rollback depends on this version; finish recovery before deletion.");
        var pins = await db.Tenants.AsNoTracking().Where(x => x.AutoUpdateTargetVersion == version).Select(x => x.Name).ToListAsync(ct);
        if (pins.Count != 0) blockers.Add($"Tenant rollout pins still reference this version: {string.Join(", ", pins)}.");
        if (await db.ClientInstallGrants.AnyAsync(x => x.ArtifactVersion == version && x.RevokedAtUtc == null && x.ExpiresAtUtc > now, ct))
            blockers.Add("Revoke or expire install links for this version before deletion.");
        var keys = releases.Select(x => x.ArtifactKey).ToArray();
        if (await db.ClientUpdateReleases.AnyAsync(x => x.Version != version && keys.Contains(x.ArtifactKey), ct))
            blockers.Add("Another release shares an archive; shared files cannot be purged.");
        // ArtifactKey is an ownership assertion, not permission to follow arbitrary stored paths.
        foreach (var runtime in runtimes)
        {
            var metadataPath = Path.Combine(PackagePath(runtime, version), "metadata.json");
            if (!File.Exists(metadataPath)) continue;
            try
            {
                ValidateOwnedTree(metadataPath);
                using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath, ct));
                var root = metadata.RootElement;
                if (root.GetProperty("rid").GetString() != runtime || root.GetProperty("version").GetString() != version)
                    blockers.Add("A package directory contains metadata owned by another runtime or version.");
                if (root.TryGetProperty("importOperationId", out var operationId) && operationId.ValueKind != JsonValueKind.Null &&
                    operationId.TryGetGuid(out var id) && !imports.Any(x => x.Id == id))
                    blockers.Add("A package belongs to another import operation; shared data cannot be purged.");
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or IOException)
            { blockers.Add("Package ownership metadata is unreadable; repair ownership before deletion."); }
        }
        foreach (var release in releases)
            if (!release.ArtifactKey.StartsWith($"{release.RuntimeId}/{version}/", StringComparison.Ordinal) ||
                release.ArtifactKey.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(release.ArtifactKey))
                blockers.Add("A release has an unsafe or shared storage key; repair its ownership before deletion.");
        return blockers;
    }

    private IEnumerable<string> Paths(string version, string[] runtimes, Guid[] imports, Guid[] uploads)
    {
        foreach (var runtime in runtimes) { ValidateRuntime(runtime); yield return PackagePath(runtime, version); }
        foreach (var id in imports)
        {
            yield return Path.Combine(Root, ".import-work", id.ToString("N"));
            yield return Path.Combine(Root, ".import-staging", id.ToString("N"));
            yield return Path.Combine(Root, ".imports", $"{id:N}.complete");
        }
        foreach (var id in uploads) yield return UploadStagingPath(id);
    }
    private string UploadStagingPath(Guid id) => Path.Combine(Root, ".upload-staging", id.ToString("N"));
    private async Task<List<(Guid Id, string RuntimeId)>> OwnedUploadStagingAsync(string version, CancellationToken ct)
    {
        var result = new List<(Guid, string)>();
        var root = Path.Combine(Root, ".upload-staging");
        if (!Directory.Exists(root)) return result;
        ValidateOwnedTree(root);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
            var runtime = await UploadStagingRuntimeAsync(directory, version, ct);
            if (runtime is not null) result.Add((id, runtime));
        }
        return result;
    }
    private async Task<string?> UploadStagingRuntimeAsync(string directory, string version, CancellationToken ct)
    {
        ValidateOwnedTree(directory);
        // New uploads journal ownership before the first archive byte. Metadata also proves
        // ownership for completed staging directories left by the earlier upload path.
        var path = Path.Combine(directory, "ownership.json");
        if (!File.Exists(path)) path = Path.Combine(directory, "metadata.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("version", out var ownerVersion) || ownerVersion.ValueKind != JsonValueKind.String ||
                ownerVersion.GetString() != version || !document.RootElement.TryGetProperty("rid", out var ownerRuntime) ||
                ownerRuntime.ValueKind != JsonValueKind.String) return null;
            var runtime = ownerRuntime.GetString();
            return runtime is not null && Runtimes.Contains(runtime) ? runtime : null;
        }
        catch (JsonException) { return null; }
    }
    private string PackagePath(string runtime, string version) => Path.Combine(Root, runtime, version);
    private static long Size(string path) => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
        : File.Exists(path) ? new FileInfo(path).Length : 0;
    private void ValidateOwnedTree(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Cleanup path is outside the managed artifact store.");
        // Check every existing ancestor, including the root, before traversing anything.
        for (var node = full; node is not null && node.Length >= Root.Length; node = Path.GetDirectoryName(node))
            if ((File.Exists(node) || Directory.Exists(node)) && (File.GetAttributes(node) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A storage path is a symbolic link or shared path; deletion is blocked.");
        if (Directory.Exists(full)) CheckChildren(full);
    }
    private static void CheckChildren(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A package contains a symbolic link or shared path; deletion is blocked.");
            if ((attributes & FileAttributes.Directory) != 0) CheckChildren(entry);
        }
    }
    private static void ValidateVersion(string version)
    {
        if (!NuGetVersion.TryParse(version, out var parsed) || parsed.ToNormalizedString() != version)
            throw new ArgumentException("Version must be a canonical semantic version.", nameof(version));
    }
    private static void ValidateRuntime(string runtime)
    {
        if (!Runtimes.Contains(runtime)) throw new IOException("Cleanup contains an unsupported runtime identifier.");
    }
    private static ClientReleaseDeletionStatus Status(ClientReleaseDeletionReceipt receipt) =>
        new(receipt.Id, receipt.Version, receipt.State, receipt.Suppressed, receipt.UpdatedAtUtc, receipt.Error);
}

public sealed class ClientReleaseDeletionWorker(IServiceScopeFactory scopes,
    ILogger<ClientReleaseDeletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var versions = await db.ClientReleaseDeletionReceipts.AsNoTracking()
                    .Where(x => x.Suppressed && (x.State == "Pending" || x.State == "Failed"))
                    .Select(x => x.Version).Take(20).ToListAsync(stoppingToken);
                foreach (var version in versions)
                    await scope.ServiceProvider.GetRequiredService<ClientReleaseDeletionService>().ResumeAsync(version, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Local client release cleanup recovery failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

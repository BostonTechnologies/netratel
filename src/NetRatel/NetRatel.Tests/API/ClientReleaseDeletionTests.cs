using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Application.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientReleaseDeletionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"release-purge-{Guid.NewGuid():N}");
    private DbContextOptions<OrchestratorDbContext> _options = null!;
    private const string Version = "1.2.3";
    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options;
        await using var db = new OrchestratorDbContext(_options);
        await db.Database.MigrateAsync();
    }
    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task PurgeDeletesOwnedPackagesImportsAndFilesWhileKeepingAttemptSnapshotsAndSuppression()
    {
        await using var db = new OrchestratorDbContext(_options);
        var (release, import) = await SeedAsync(db, history: true);
        var service = Service(db);
        var preview = await service.PreviewAsync(Version, default);
        Assert.Empty(preview.Blockers);
        Assert.Equal(1, preview.HistoricalAttempts);
        Assert.True(preview.ReclaimableBytes > 0);

        var result = await service.DeleteAsync(Version, "fixture-publisher", default);
        Assert.Equal("Deleted", result.State);
        Assert.Empty(await db.ClientUpdateReleases.ToListAsync());
        Assert.Empty(await db.ClientReleaseImportOperations.ToListAsync());
        Assert.False(Directory.Exists(Path.Combine(_root, "linux-x64", Version)));
        Assert.False(Directory.Exists(Path.Combine(_root, ".import-work", import.Id.ToString("N"))));
        Assert.False(Directory.Exists(Path.Combine(_root, ".import-staging", import.Id.ToString("N"))));
        Assert.False(File.Exists(Path.Combine(_root, ".imports", $"{import.Id:N}.complete")));
        var attempt = await db.ClientUpdateAttempts.SingleAsync();
        Assert.Equal(release.Id, attempt.ReleaseId);
        Assert.Null(attempt.ReleaseRecordId);
        Assert.Equal(release.PublicId, attempt.PurgedReleasePublicId);
        Assert.Equal(Version, attempt.TargetVersion);
        Assert.True(await ClientReleaseDeletionService.IsSuppressedAsync(db, Version, default));
        Assert.Equal(result.Id, (await service.DeleteAsync(Version, "fixture-publisher", default)).Id);
        await Artifacts(db).CompleteImportVisibilityAsync(import.Id, default);
        Assert.False(File.Exists(Path.Combine(_root, ".imports", $"{import.Id:N}.complete")));
    }

    [Fact]
    public async Task TerminalRollbackReportCanBeReplayedAfterPurgeUsingHistoricalReleaseIdentity()
    {
        await using var db = new OrchestratorDbContext(_options);
        var (release, _) = await SeedAsync(db, history: true);
        var attempt = await db.ClientUpdateAttempts.SingleAsync();
        attempt.State = ClientUpdateAttemptState.RolledBack;
        await db.SaveChangesAsync();
        Assert.Equal("Deleted", (await Service(db).DeleteAsync(Version, "fixture", default)).State);

        var authority = new ClientUpdateAuthorityService(db, new UpdateCatalog(), TimeProvider.System,
            NullLogger<ClientUpdateAuthorityService>.Instance);
        await authority.ReportAsync(new AuthenticatedAgentIdentity(attempt.TenantId, attempt.AgentId),
            attempt.PublicId, ClientUpdateAttemptState.RolledBack, "fixture-rollback", "replayed terminal report", default);

        var retained = await db.ClientUpdateAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(ClientUpdateAttemptState.RolledBack, retained.State);
        Assert.Equal(release.Id, retained.ReleaseId);
        Assert.Null(retained.ReleaseRecordId);
        Assert.Equal(release.PublicId, retained.PurgedReleasePublicId);
        Assert.Equal(release.PublicId, (await db.AgentClientUpdateStates.AsNoTracking().SingleAsync()).SuppressedReleaseId);
        Assert.Empty(await db.ClientUpdateReleases.ToListAsync());
        Assert.False(Directory.Exists(Path.Combine(_root, "linux-x64", Version)));
    }

    [Fact]
    public async Task FileFailureKeepsRecoverableJournalAndRestartFinishesWithoutFalseSuccess()
    {
        await using (var db = new OrchestratorDbContext(_options))
        {
            await SeedAsync(db);
            var failed = await Service(db, () => throw new IOException("fixture cleanup interruption"))
                .DeleteAsync(Version, "fixture", default);
            Assert.Equal("Failed", failed.State);
            Assert.Contains("fixture cleanup interruption", failed.Error);
            Assert.Single(await db.ClientUpdateReleases.ToListAsync());
            Assert.True(await ClientReleaseDeletionService.IsSuppressedAsync(db, Version, default));
        }
        // A fresh DbContext represents worker recovery after restart; missing already-deleted
        // paths are idempotent and metadata is removed only after all cleanup succeeds.
        await using var restarted = new OrchestratorDbContext(_options);
        var complete = await Service(restarted).ResumeAsync(Version, default);
        Assert.Equal("Deleted", complete.State);
        Assert.Empty(await restarted.ClientUpdateReleases.ToListAsync());
        Assert.Empty(await restarted.ClientReleaseImportOperations.ToListAsync());
    }

    [Fact]
    public async Task MutationRechecksPinsDownloadsImportsAndRollbackDependencies()
    {
        await using var db = new OrchestratorDbContext(_options);
        var (release, import) = await SeedAsync(db, history: true);
        var service = Service(db);
        Assert.Empty((await service.PreviewAsync(Version, default)).Blockers);
        var tenant = await db.Tenants.SingleAsync();
        tenant.AutoUpdateTargetVersion = Version;
        import.State = ClientReleaseImportState.Downloading;
        import.LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(1);
        var attempt = await db.ClientUpdateAttempts.SingleAsync();
        attempt.State = ClientUpdateAttemptState.RollbackUnverified;
        db.ClientArtifactDownloadLeases.Add(new() { Id = Guid.NewGuid(), RuntimeId = release.RuntimeId, Version = Version, ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5) });
        await db.SaveChangesAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(Version, "fixture", default));
        Assert.Contains("pins", exception.Message);
        Assert.Contains("download lease", exception.Message);
        Assert.Contains("imports", exception.Message);
        Assert.Contains("rollback", exception.Message);
        Assert.True(File.Exists(Path.Combine(_root, "linux-x64", Version, "archive.zip")));
        Assert.Empty(await db.ClientReleaseDeletionReceipts.ToListAsync());
    }

    [Fact]
    public async Task SharedStorageAndSymbolicLinksAreRejectedBeforeAnyPurge()
    {
        await using var db = new OrchestratorDbContext(_options);
        await SeedAsync(db);
        db.ClientUpdateReleases.Add(new() { PublicId = Guid.NewGuid(), Revision = 2, RuntimeId = "win-x64", Version = "1.2.4",
            Enabled = false, ArtifactKey = $"linux-x64/{Version}/archive.zip", Sha256 = new string('a', 64), ManifestJson = "{}" });
        await db.SaveChangesAsync();
        var external = Path.Combine(_root, "keep.txt");
        await File.WriteAllTextAsync(external, "shared content");
        File.CreateSymbolicLink(Path.Combine(_root, "linux-x64", Version, "shared.txt"), external);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).DeleteAsync(Version, "fixture", default));
        Assert.Contains("shares", exception.Message);
        Assert.Contains("symbolic link", exception.Message);
        Assert.True(File.Exists(external));
        Assert.Single(await db.ClientReleaseImportOperations.ToListAsync());
    }

    [Fact]
    public async Task StoredArchiveDownloadLeaseBlocksPurgeAndDisposalDrainsIt()
    {
        await using var db = new OrchestratorDbContext(_options);
        await SeedAsync(db);
        var download = await Artifacts(db).DownloadRawAsync("linux-x64", Version, default);
        Assert.Single(await db.ClientArtifactDownloadLeases.ToListAsync());
        Assert.Contains((await Service(db).PreviewAsync(Version, default)).Blockers, x => x.Contains("download lease"));
        await download.Content.DisposeAsync();
        Assert.Empty(await db.ClientArtifactDownloadLeases.AsNoTracking().ToListAsync());
        Assert.Equal("Deleted", (await Service(db).DeleteAsync(Version, "fixture", default)).State);
    }

    [Fact]
    public async Task ActiveManualUploadBlocksPurgeAndRestartCleansOnlyProvenOwnedStaging()
    {
        await using var uploadDb = new OrchestratorDbContext(_options);
        var file = new PausedUpload();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var upload = Artifacts(uploadDb).UploadAsync(file, "linux-x64", Version, null, "fixture", cancellation.Token);
        await file.Started.Task.WaitAsync(cancellation.Token);
        await using (var db = new OrchestratorDbContext(_options))
        {
            await SeedAsync(db);
            var preview = await Service(db).PreviewAsync(Version, default);
            Assert.Single(preview.UploadStagingIds!);
            Assert.Contains(preview.Blockers, x => x.Contains("upload staging lease"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).DeleteAsync(Version, "fixture", default));
        }
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);

        var ownedId = Guid.NewGuid();
        var owned = Path.Combine(_root, ".upload-staging", ownedId.ToString("N"));
        Directory.CreateDirectory(owned);
        await File.WriteAllTextAsync(Path.Combine(owned, "ownership.json"),
            JsonSerializer.Serialize(new { rid = "linux-x64", version = Version }));
        await File.WriteAllTextAsync(Path.Combine(owned, "partial.zip"), "crashed-upload-bytes");
        var unowned = Path.Combine(_root, ".upload-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unowned);
        await File.WriteAllTextAsync(Path.Combine(unowned, "unclaimed.bin"), "keep");
        await File.WriteAllTextAsync(Path.Combine(unowned, "ownership.json"), "{\"version\":1,\"rid\":\"linux-x64\"}");
        var otherVersion = Path.Combine(_root, ".upload-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherVersion);
        await File.WriteAllTextAsync(Path.Combine(otherVersion, "metadata.json"),
            JsonSerializer.Serialize(new { rid = "linux-x64", version = "1.2.4" }));

        await using var restarted = new OrchestratorDbContext(_options);
        Assert.Empty(await restarted.ClientArtifactDownloadLeases.ToListAsync());
        restarted.ClientArtifactDownloadLeases.Add(new() { Id = ownedId, RuntimeId = "linux-x64", Version = Version,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await restarted.SaveChangesAsync();
        Assert.Equal(new[] { ownedId }, (await Service(restarted).PreviewAsync(Version, default)).UploadStagingIds);
        Assert.Equal("Deleted", (await Service(restarted).DeleteAsync(Version, "fixture", default)).State);
        Assert.False(Directory.Exists(owned));
        Assert.True(File.Exists(Path.Combine(unowned, "unclaimed.bin")));
        Assert.True(File.Exists(Path.Combine(otherVersion, "metadata.json")));
        Assert.Empty(await restarted.ClientArtifactDownloadLeases.ToListAsync());
    }

    [Fact]
    public async Task AutomaticQueueCannotReimportAPurgedVersionButDeliberateImportCan()
    {
        await using var db = new OrchestratorDbContext(_options);
        var (_, deletedImport) = await SeedAsync(db);
        await Service(db).DeleteAsync(Version, "fixture", default);
        var imports = new ClientReleaseImportService(db, new Catalog(), null!, null!, TimeProvider.System);
        Assert.Null(await imports.QueueAsync(42, "client-release-automation", default));
        Assert.Empty(await db.ClientReleaseImportOperations.ToListAsync());
        Assert.True(await ClientReleaseDeletionService.IsSuppressedAsync(db, Version, default));
        Assert.NotNull(await imports.QueueAsync(42, "fixture deliberate importer", default));
        Assert.False(await ClientReleaseDeletionService.IsSuppressedAsync(db, Version, default));
        Assert.Equal("Deleted", (await db.ClientReleaseDeletionReceipts.SingleAsync()).State);
        await Artifacts(db).CompleteImportVisibilityAsync(deletedImport.Id, default);
        Assert.False(File.Exists(Path.Combine(_root, ".imports", $"{deletedImport.Id:N}.complete")));
    }
    private sealed class Catalog : IGitHubClientReleaseCatalog
    {
        public Task<GitHubClientRelease?> FindAsync(long id, CancellationToken ct) => Task.FromResult<GitHubClientRelease?>(
            new(id, "v1.2.3", Version, Version, DateTimeOffset.UtcNow, false, "https://example.invalid", [], 0, "verification required"));
        public Task<GitHubClientReleasePage> ListAsync(string channel, int page, bool refresh, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> ResolveTagCommitAsync(string tag, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class UpdateCatalog : IClientUpdateCatalog
    {
        public ClientUpdateOfferSnapshot? GetOffer(int tenantId, Guid agentId, string runtimeId, string currentVersion, string channel) => null;
        public ClientUpdatePolicySnapshot GetPolicy(int tenantId, Guid agentId, long clientPolicyRevision) => new(0, false, false, false, null);
        public long Revision => 0;
        public DateTimeOffset RefreshedAtUtc => DateTimeOffset.UtcNow;
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PausedUpload : IFormFile
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ContentType => "application/zip";
        public string ContentDisposition => "";
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length => 3;
        public string Name => "file";
        public string FileName => "upload.zip";
        public Stream OpenReadStream() => throw new NotSupportedException();
        public void CopyTo(Stream target) => throw new NotSupportedException();
        public async Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
        {
            await target.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private async Task<(ClientUpdateReleaseRecord, ClientReleaseImportOperation)> SeedAsync(OrchestratorDbContext db, bool history = false)
    {
        var import = new ClientReleaseImportOperation { Id = Guid.NewGuid(), GitHubReleaseId = 42, Tag = "v1.2.3", Version = Version,
            State = ClientReleaseImportState.Imported, RequestedBy = "fixture", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
            Assets = [new() { RuntimeId = "linux-x64", SourceName = "source.zip", SourceSha256 = new string('a', 64), State = ClientReleaseImportAssetState.Imported }] };
        var release = new ClientUpdateReleaseRecord { PublicId = Guid.NewGuid(), Revision = 1, RuntimeId = "linux-x64", Version = Version,
            Enabled = false, ArtifactKey = $"linux-x64/{Version}/archive.zip", Sha256 = new string('a', 64), SizeBytes = 3, ManifestJson = "{}" };
        db.ClientUpdateReleases.Add(release);
        db.ClientReleaseImportOperations.Add(import);
        if (history) db.Tenants.Add(new() { Id = 7, Name = "Disposable" });
        await db.SaveChangesAsync();
        if (history)
        {
            var agent = new Agent { Id = Guid.NewGuid(), TenantId = 7, Name = "Fixture" };
            db.Agents.Add(agent);
            db.ClientUpdateAttempts.Add(new() { PublicId = Guid.NewGuid(), ReleaseId = release.Id, TenantId = 7, AgentId = agent.Id,
                RuntimeId = release.RuntimeId, FromVersion = "1.2.2", TargetVersion = Version, State = ClientUpdateAttemptState.Accepted,
                AdmissionNonceHash = new string('b', 64), CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var package = Path.Combine(_root, "linux-x64", Version);
        Directory.CreateDirectory(package);
        await File.WriteAllTextAsync(Path.Combine(package, "metadata.json"), JsonSerializer.Serialize(new { rid = "linux-x64", version = Version, fileName = "archive.zip", sha256 = new string('a', 64), size = 3, importOperationId = import.Id }));
        await File.WriteAllTextAsync(Path.Combine(package, "archive.zip"), "abc");
        foreach (var area in new[] { ".import-work", ".import-staging" })
        {
            var path = Path.Combine(_root, area, import.Id.ToString("N"));
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "staged"), "fixture");
        }
        Directory.CreateDirectory(Path.Combine(_root, ".imports"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".imports", $"{import.Id:N}.complete"), "complete");
        return (release, import);
    }
    private ClientArtifactsService Artifacts(OrchestratorDbContext db) =>
        new(new Environment(), Options.Create(new ClientArtifactsOptions { StorageRoot = _root }), new ConfigurationBuilder().Build(),
            NullLogger<ClientArtifactsService>.Instance, Options.Create(new AgentAuthOptions()), null!, null!, null!, null!, null!, db, TimeProvider.System);
    private ClientReleaseDeletionService Service(OrchestratorDbContext db, Action? hook = null) =>
        new(db, Options.Create(new ClientArtifactsOptions { StorageRoot = _root, TestDeletionHook = hook }), new Environment(), TimeProvider.System);
    private sealed class Environment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Fixture";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}

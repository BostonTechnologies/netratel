using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Models;
using NetRatel.API.Services;
using NetRatel.Akka.Configuration;
using NetRatel.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientReleaseImportWorkerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"client-import-worker-{Guid.NewGuid():N}");
    private DbContextOptions<OrchestratorDbContext> _dbOptions = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _dbOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options;
        await using var db = new OrchestratorDbContext(_dbOptions);
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ImportMakesPackVisibleOnlyAfterEveryRuntimeVerifies(bool corruptSecondRuntime, bool conflictOnPublish)
    {
        var fixture = CreateFixture(corruptSecondRuntime);
        var store = new RecordingArtifactStore();
        await using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()))
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton(new NetRatelAkkaMigrationOptions())
            .AddSingleton<ClientUpdateCatalog>()
            .AddSingleton<IClientUpdateCatalog>(provider => provider.GetRequiredService<ClientUpdateCatalog>())
            .AddScoped<ClientUpdateAuthorityService>()
            .AddSingleton<IGitHubClientReleaseCatalog>(new FixtureCatalog(fixture.Release, fixture.Commit))
            .AddSingleton<IClientArtifactsService>(store)
            .AddSingleton<IWebHostEnvironment>(new FixtureEnvironment(_storage))
            .AddSingleton<IOptions<ClientArtifactsOptions>>(Options.Create(new ClientArtifactsOptions { StorageRoot = _storage }))
            .AddScoped<GitHubClientAssetDownloader>(_ => new GitHubClientAssetDownloader(
                new HttpClient(new AssetHandler(fixture.Bytes)) { Timeout = Timeout.InfiniteTimeSpan },
                new ConfigurationBuilder().Build()))
            .BuildServiceProvider();

        var id = Guid.NewGuid();
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.ClientReleaseImportOperations.Add(new ClientReleaseImportOperation
            {
                Id = id, GitHubReleaseId = fixture.Release.Id, Tag = fixture.Release.Tag,
                Version = fixture.Release.Version, RequestedBy = "fixture-admin",
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var worker = ActivatorUtilities.CreateInstance<ClientReleaseImportWorker>(services);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var observation = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            ClientReleaseImportOperation? operation;
            do
            {
                await observation.WaitForNextTickAsync(timeout.Token);
                await using var scope = services.CreateAsyncScope();
                operation = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
                    .ClientReleaseImportOperations.AsNoTracking().Include(x => x.Assets)
                    .SingleAsync(x => x.Id == id, timeout.Token);
            } while (operation.State is not (ClientReleaseImportState.Imported or ClientReleaseImportState.Failed) ||
                     !corruptSecondRuntime && operation.State == ClientReleaseImportState.Imported && !store.Visible);

            if (corruptSecondRuntime)
            {
                Assert.Equal(ClientReleaseImportState.Failed, operation.State);
                Assert.Empty(store.ImportedRuntimes);
                Assert.False(store.Visible);
            }
            else
            {
                Assert.True(operation.State == ClientReleaseImportState.Imported, operation.Error);
                Assert.Equal(2, operation.Assets.Count);
                Assert.All(operation.Assets, asset => Assert.Equal(ClientReleaseImportAssetState.Imported, asset.State));
                Assert.Equal(["linux-x64", "win-x64"], store.ImportedRuntimes.OrderBy(x => x, StringComparer.Ordinal));
                Assert.True(store.Visible);
                Assert.Null(operation.PublishedAtUtc);
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                Assert.Empty(await db.ClientUpdateReleases.ToListAsync());
                if (conflictOnPublish)
                {
                    db.ClientUpdateReleases.Add(new ClientUpdateReleaseRecord
                    {
                        PublicId = Guid.NewGuid(), RuntimeId = "win-x64", Version = operation.Version,
                        Channel = "stable", ArtifactKey = "win-x64/existing.zip", Sha256 = new string('f', 64),
                        SizeBytes = 1, ManifestJson = "{}", Enabled = false,
                        PublishedAtUtc = DateTimeOffset.UtcNow
                    });
                    await db.SaveChangesAsync();
                }
                var pack = operation.Assets.Select(asset => new ClientPackPublishItem(
                    new ClientArtifactSummaryDto
                    {
                        Rid = asset.RuntimeId, Version = operation.Version,
                        FileName = $"NetRatel.Client-{asset.RuntimeId}-{operation.Version}.zip",
                        Size = asset.LocalSizeBytes!.Value, Sha256 = asset.LocalSha256!
                    }, "{}")) .ToArray();
                var authority = scope.ServiceProvider.GetRequiredService<ClientUpdateAuthorityService>();
                if (conflictOnPublish)
                {
                    await Assert.ThrowsAsync<ClientArtifactConflictException>(() => authority.PublishImportedPackAsync(
                        id, pack, "fixture-admin", false, timeout.Token));
                    Assert.Equal(1, await db.ClientUpdateReleases.CountAsync());
                    Assert.False(await db.ClientUpdateReleases.AnyAsync(x => x.RuntimeId == "linux-x64"));
                }
                else
                {
                    await authority.PublishImportedPackAsync(id, pack, "fixture-admin", false, timeout.Token);
                    Assert.Equal(2, await db.ClientUpdateReleases.CountAsync());
                    Assert.True((await db.ClientReleaseImportOperations.SingleAsync(x => x.Id == id)).PublishedAtUtc.HasValue);
                }
            }
        }
        finally { await worker.StopAsync(CancellationToken.None); worker.Dispose(); }
    }

    private static Fixture CreateFixture(bool corruptSecondRuntime)
    {
        const string version = "1.2.3";
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var source = new Dictionary<long, byte[]>();
        var assets = new List<GitHubClientAsset>();
        var files = new Dictionary<string, object>();
        var artifacts = new Dictionary<string, string>();
        var lines = new List<string>();
        long id = 10;
        foreach (var rid in new[] { "linux-x64", "win-x64" })
        {
            var name = $"netratel-client-{version}-{rid}.zip";
            var bytes = MakeArchive(rid, version, commit);
            var hash = Hash(bytes);
            source[id] = corruptSecondRuntime && rid == "win-x64" ? bytes[..^1] : bytes;
            assets.Add(new GitHubClientAsset(id, name, rid, bytes.Length, "sha256:" + hash));
            files[name] = new { sha256 = hash };
            artifacts[name] = hash;
            lines.Add($"{hash}  {name}");
            id++;
        }
        var publication = JsonSerializer.SerializeToUtf8Bytes(new
        {
            productVersion = version, publicCommit = commit,
            verification = new { state = "complete" },
            inputReceipt = new { repository = "BostonTechnologies/netratel", productVersion = version,
                headSha = commit, files }, artifacts
        });
        var sums = Encoding.UTF8.GetBytes(string.Join('\n', lines.Append($"{Hash(publication)}  publication.json")) + "\n");
        source[20] = publication;
        source[21] = sums;
        var release = new GitHubClientRelease(100 + Random.Shared.NextInt64(1, 1_000_000),
            "v" + version, version, "fixture", DateTimeOffset.UtcNow, false,
            "https://github.com/BostonTechnologies/netratel/releases/tag/v1.2.3",
            assets, assets.Sum(x => x.SizeBytes), "verification required")
        {
            PublicationAsset = new GitHubReleaseEvidenceAsset(20, "publication.json", publication.Length,
                "sha256:" + Hash(publication)),
            ChecksumsAsset = new GitHubReleaseEvidenceAsset(21, "SHA256SUMS", sums.Length,
                "sha256:" + Hash(sums))
        };
        return new Fixture(release, commit, source);
    }

    private static byte[] MakeArchive(string runtime, string version, string commit)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var prefix = $"netratel-client-{runtime}/";
            var executable = runtime.StartsWith("win-", StringComparison.Ordinal) ? "NetRatel.Client.exe" : "NetRatel.Client";
            var manifest = archive.CreateEntry(prefix + "netratel-client-manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write(JsonSerializer.Serialize(new
                {
                    schema = "netratel.client.manifest.v1", product = "NetRatel.Client",
                    version, runtimeId = runtime, commitSha = commit, executable
                }));
            var binary = archive.CreateEntry(prefix + executable);
            binary.ExternalAttributes = (0x8000 | 0x1ED) << 16;
            using var content = new StreamWriter(binary.Open());
            content.Write("fixture executable bytes");
        }
        return buffer.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Fixture(GitHubClientRelease Release, string Commit, Dictionary<long, byte[]> Bytes);

    private sealed class FixtureCatalog(GitHubClientRelease release, string commit) : IGitHubClientReleaseCatalog
    {
        public Task<GitHubClientReleasePage> ListAsync(string channel, int page, bool refresh, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<GitHubClientRelease?> FindAsync(long releaseId, CancellationToken cancellationToken) =>
            Task.FromResult<GitHubClientRelease?>(releaseId == release.Id ? release : null);
        public Task<string> ResolveTagCommitAsync(string tag, CancellationToken cancellationToken) =>
            Task.FromResult(commit);
    }

    private sealed class AssetHandler(Dictionary<long, byte[]> bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var id = long.Parse(request.RequestUri!.Segments[^1]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes[id])
            });
        }
    }

    private sealed class FixtureEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "NetRatel.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
    }

    private sealed class RecordingArtifactStore : IClientArtifactsService
    {
        public List<string> ImportedRuntimes { get; } = [];
        public bool Visible { get; private set; }
        public Task<ClientArtifactUploadResultDto> ImportVerifiedAsync(IFormFile file, string rid, string version,
            ClientArtifactImportProvenance provenance, string? importedBy, CancellationToken ct)
        {
            ImportedRuntimes.Add(rid);
            return Task.FromResult(new ClientArtifactUploadResultDto());
        }
        public Task CompleteImportVisibilityAsync(Guid operationId, CancellationToken ct)
        {
            Visible = true;
            return Task.CompletedTask;
        }
        public Task<ClientArtifactListDto> ListAsync(string? rid, int skip, int take, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactSummaryDto?> GetLatestAsync(string rid, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactSummaryDto?> GetMetadataAsync(string rid, string version, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactUploadResultDto> UploadAsync(IFormFile file, string rid, string version, string? notes, string? uploadedBy, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> DownloadAsync(string rid, string versionOrLatest, bool allowFallback, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> DownloadRawAsync(string rid, string versionOrLatest, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> DownloadForClientAsync(ClientDownloadRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string rid, string version, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> RunFallbackScanAsync(string rid, string? version, CancellationToken ct) => throw new NotSupportedException();
    }
}

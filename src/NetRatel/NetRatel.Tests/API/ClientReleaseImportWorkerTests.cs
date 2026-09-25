using System.IO.Compression;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Models;
using NetRatel.API.Services;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Agents;
using NetRatel.Application.Artifacts;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Artifacts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
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
        var assetRequests = new ConcurrentDictionary<long, int>();
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
                new HttpClient(new AssetHandler(fixture.Bytes, id => assetRequests.AddOrUpdate(id, 1, (_, count) => count + 1))) { Timeout = Timeout.InfiniteTimeSpan },
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
                var firstAsset = fixture.Release.ClientAssets.Single(x => x.RuntimeId == "linux-x64");
                var stagedFirstAsset = Directory.EnumerateFiles(
                        Path.Combine(_storage, ".import-work", id.ToString("N")), firstAsset.Name,
                        SearchOption.AllDirectories).SingleOrDefault();
                Assert.NotNull(stagedFirstAsset);
                Assert.Equal(1, assetRequests[firstAsset.Id]);

                // A short transfer must leave the verified first runtime reusable, while
                // retrying the failed second runtime against the same immutable evidence.
                var secondAsset = fixture.Release.ClientAssets.Single(x => x.RuntimeId == "win-x64");
                fixture.Bytes[secondAsset.Id] = fixture.OriginalBytes[secondAsset.Id];
                await using (var retryScope = services.CreateAsyncScope())
                {
                    var retryDb = retryScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                    await retryDb.ClientReleaseImportOperations.Where(x => x.Id == id)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.State, ClientReleaseImportState.Queued)
                            .SetProperty(x => x.Error, (string?)null), timeout.Token);
                }
                do
                {
                    await observation.WaitForNextTickAsync(timeout.Token);
                    await using var retryScope = services.CreateAsyncScope();
                    operation = await retryScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
                        .ClientReleaseImportOperations.AsNoTracking().Include(x => x.Assets)
                        .SingleAsync(x => x.Id == id, timeout.Token);
                } while (operation.State is not (ClientReleaseImportState.Imported or ClientReleaseImportState.Failed));

                Assert.True(operation.State == ClientReleaseImportState.Imported, operation.Error);
                Assert.Equal(2, operation.AttemptCount);
                Assert.Equal(1, assetRequests[firstAsset.Id]);
                Assert.Equal(2, assetRequests[secondAsset.Id]);
                Assert.True(store.Visible);
                Assert.Equal(2, store.ImportedRuntimes.Count);
                Assert.Null(operation.PublishedAtUtc);
                await using var verifyScope = services.CreateAsyncScope();
                Assert.Empty(await verifyScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
                    .ClientUpdateReleases.ToListAsync(timeout.Token));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoWorkersClaimQueuedOrStalePackOnceAndExposeAllRuntimesTogether(bool staleLease)
    {
        var fixture = CreateFixture(corruptSecondRuntime: false);
        var store = new RecordingArtifactStore();
        await using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()))
            .AddSingleton<TimeProvider>(TimeProvider.System)
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
                State = staleLease ? ClientReleaseImportState.Importing : ClientReleaseImportState.Queued,
                LeaseOwner = staleLease ? Guid.NewGuid() : null,
                LeaseUntilUtc = staleLease ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
                AttemptCount = staleLease ? 1 : 0,
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var first = ActivatorUtilities.CreateInstance<ClientReleaseImportWorker>(services);
        var second = ActivatorUtilities.CreateInstance<ClientReleaseImportWorker>(services);
        await Task.WhenAll(first.StartAsync(CancellationToken.None), second.StartAsync(CancellationToken.None));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var observation = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            ClientReleaseImportOperation operation;
            do
            {
                await observation.WaitForNextTickAsync(timeout.Token);
                await using var scope = services.CreateAsyncScope();
                operation = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
                    .ClientReleaseImportOperations.AsNoTracking().SingleAsync(x => x.Id == id, timeout.Token);
            } while (operation.State is not (ClientReleaseImportState.Imported or ClientReleaseImportState.Failed));

            Assert.Equal(ClientReleaseImportState.Imported, operation.State);
            Assert.Equal(staleLease ? 2 : 1, operation.AttemptCount);
            Assert.True(store.Visible);
            Assert.Equal(["linux-x64", "win-x64"], store.ImportedRuntimes.OrderBy(x => x, StringComparer.Ordinal));
            Assert.Null(operation.PublishedAtUtc);
        }
        finally
        {
            await Task.WhenAll(first.StopAsync(CancellationToken.None), second.StopAsync(CancellationToken.None));
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public async Task CancellationDuringDownloadCanRetryWithoutPartiallyExposingOrPublishingPack()
    {
        var fixture = CreateFixture(corruptSecondRuntime: false);
        var store = new RecordingArtifactStore();
        var enteredDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = 0;
        await using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()))
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<IGitHubClientReleaseCatalog>(new FixtureCatalog(fixture.Release, fixture.Commit))
            .AddSingleton<IClientArtifactsService>(store)
            .AddSingleton<IWebHostEnvironment>(new FixtureEnvironment(_storage))
            .AddSingleton<IOptions<ClientArtifactsOptions>>(Options.Create(new ClientArtifactsOptions { StorageRoot = _storage }))
            .AddScoped<GitHubClientAssetDownloader>(_ => new GitHubClientAssetDownloader(
                new HttpClient(new AssetHandler(fixture.Bytes, wait: async (assetId, ct) =>
                {
                    if (assetId != 11 || Interlocked.Exchange(ref blocked, 1) != 0) return;
                    enteredDownload.SetResult();
                    await releaseDownload.Task.WaitAsync(ct);
                })) { Timeout = Timeout.InfiniteTimeSpan }, new ConfigurationBuilder().Build()))
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
            await enteredDownload.Task.WaitAsync(timeout.Token);
            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                Assert.Equal(1, await db.ClientReleaseImportOperations.Where(x => x.Id == id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CancellationRequested, true), timeout.Token));
            }
            releaseDownload.SetResult();
            await WaitForStateAsync(services, id, ClientReleaseImportState.Cancelled, timeout.Token);
            Assert.False(store.Visible);
            Assert.Empty(store.ImportedRuntimes);

            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                Assert.Equal(1, await db.ClientReleaseImportOperations.Where(x => x.Id == id && x.State == ClientReleaseImportState.Cancelled)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.State, ClientReleaseImportState.Queued)
                        .SetProperty(x => x.CancellationRequested, false)
                        .SetProperty(x => x.Error, (string?)null), timeout.Token));
            }
            await WaitForStateAsync(services, id, ClientReleaseImportState.Imported, timeout.Token);
            Assert.True(store.Visible);
            Assert.Equal(["linux-x64", "win-x64"], store.ImportedRuntimes.OrderBy(x => x, StringComparer.Ordinal));
            await using var verifyScope = services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            Assert.Null((await verifyDb.ClientReleaseImportOperations.SingleAsync(x => x.Id == id, timeout.Token)).PublishedAtUtc);
            Assert.Empty(await verifyDb.ClientUpdateReleases.ToListAsync(timeout.Token));
        }
        finally
        {
            releaseDownload.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    [Trait("category", "f113-r1b")]
    public async Task ExpiredWorkerCannotOverwriteSuccessorAfterLeaseGenerationTakeover()
    {
        var fixture = CreateFixture(corruptSecondRuntime: false);
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var fence = new ImportOwnershipFenceInterceptor();
        await using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString())
                .AddInterceptors(fence))
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<IGitHubClientReleaseCatalog>(new FixtureCatalog(fixture.Release, fixture.Commit))
            .AddSingleton(new NetRatelAkkaMigrationOptions())
            .AddSingleton<ClientUpdateCatalog>()
            .AddSingleton<IClientUpdateCatalog>(provider => provider.GetRequiredService<ClientUpdateCatalog>())
            .AddScoped<ClientUpdateAuthorityService>()
            .AddScoped<ClientReleaseImportService>()
            .AddSingleton(fence)
            .AddScoped<IClientArtifactsService, ClientArtifactsService>()
            .AddScoped<IEnrollmentCodeIssueService, EnrollmentCodeIssueService>()
            .AddSingleton<IArtifactZipInjectionService, ZipInjectionService>()
            .AddSingleton<ITenantLookupService>(new FixtureTenantLookup())
            .AddSingleton<IEventRecorder>(new NoopEventRecorder())
            .AddSingleton<ICorrelationContext>(new FixtureCorrelationContext())
            .AddSingleton<IWebHostEnvironment>(new FixtureEnvironment(_storage))
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IOptions<AgentAuthOptions>>(Options.Create(new AgentAuthOptions()))
            .AddSingleton<IOptions<ClientArtifactsOptions>>(Options.Create(new ClientArtifactsOptions { StorageRoot = _storage }))
            .AddScoped<GitHubClientAssetDownloader>(_ => new GitHubClientAssetDownloader(
                new HttpClient(new AssetHandler(fixture.Bytes)) { Timeout = Timeout.InfiniteTimeSpan },
                new ConfigurationBuilder().Build()))
            .BuildServiceProvider();

        Guid id;
        await using (var scope = services.CreateAsyncScope())
        {
            var queued = await scope.ServiceProvider.GetRequiredService<ClientReleaseImportService>()
                .QueueAsync(fixture.Release.Id, "fencing-fixture", CancellationToken.None);
            Assert.NotNull(queued);
            Assert.Equal(fixture.Release.TotalClientBytes, queued!.TotalBytes);
            id = queued.Id;
        }

        var first = ActivatorUtilities.CreateInstance<ClientReleaseImportWorker>(services);
        var second = ActivatorUtilities.CreateInstance<ClientReleaseImportWorker>(services);
        await first.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await fence.FirstFenceStarted.Task.WaitAsync(timeout.Token);
            clock.Advance(TimeSpan.FromMinutes(3));
            await second.StartAsync(CancellationToken.None);
            await WaitForStateAsync(services, id, ClientReleaseImportState.Imported, timeout.Token);

            fence.ReleaseFirstFence();
            await fence.StaleFenceRejected.Task.WaitAsync(timeout.Token);

            await using var verifyScope = services.CreateAsyncScope();
            var db = verifyScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var operation = await db.ClientReleaseImportOperations.AsNoTracking().Include(x => x.Assets)
                .SingleAsync(x => x.Id == id, timeout.Token);
            Assert.Equal(ClientReleaseImportState.Imported, operation.State);
            Assert.Equal(2, operation.AttemptCount);
            Assert.Null(operation.LeaseOwner);
            Assert.Equal(2, operation.Assets.Count);
            Assert.All(operation.Assets, asset => Assert.Equal(ClientReleaseImportAssetState.Imported, asset.State));

            var fenceCommands = fence.Commands.Where(x => x.IsOwnershipFence).ToArray();
            Assert.NotEmpty(fenceCommands);
            Assert.All(fenceCommands, command => Assert.True(command.HasTransaction));
            Assert.Contains(fence.Commands, command => command.IsAssetInsert && command.HasTransaction);

            var artifacts = verifyScope.ServiceProvider.GetRequiredService<IClientArtifactsService>();
            var visible = await artifacts.ListAsync(null, 0, 20, timeout.Token);
            Assert.Equal(2, visible.Items.Count);
            foreach (var asset in operation.Assets)
            {
                var download = await artifacts.DownloadRawAsync(asset.RuntimeId, operation.Version, timeout.Token);
                await using var content = download.Content;
                Assert.Equal(asset.LocalSha256,
                    Convert.ToHexString(await SHA256.HashDataAsync(content, timeout.Token)).ToLowerInvariant());
            }
        }
        finally
        {
            fence.ReleaseFirstFence();
            await Task.WhenAll(first.StopAsync(CancellationToken.None), second.StopAsync(CancellationToken.None));
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public async Task HardStopAfterAtomicArtifactMoveRecoversPairAndRejectsConflictingOrphan()
    {
        var fixture = CreateFixture(corruptSecondRuntime: false);
        var source = fixture.Release.ClientAssets.Single(x => x.RuntimeId == "linux-x64");
        var operationId = Guid.NewGuid();
        var leaseOwner = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var archivePath = Path.Combine(_storage, "crash-probe-input.zip");
        Directory.CreateDirectory(_storage);
        await File.WriteAllBytesAsync(archivePath, fixture.OriginalBytes[source.Id]);

        await using (var seed = new OrchestratorDbContext(_dbOptions))
        {
            seed.ClientReleaseImportOperations.Add(new ClientReleaseImportOperation
            {
                Id = operationId, GitHubReleaseId = fixture.Release.Id, Tag = fixture.Release.Tag,
                Version = fixture.Release.Version, RequestedBy = "crash-probe", State = ClientReleaseImportState.Importing,
                LeaseOwner = leaseOwner, LeaseGeneration = 1, LeaseUntilUtc = now.AddMinutes(2),
                CreatedAtUtc = now, UpdatedAtUtc = now,
                Assets = [new ClientReleaseImportAsset
                {
                    OperationId = operationId, RuntimeId = source.RuntimeId, GitHubAssetId = source.Id,
                    SourceName = source.Name, SourceSha256 = source.Sha256Digest![7..],
                    SourceSizeBytes = source.SizeBytes, LocalSha256 = Hash(fixture.OriginalBytes[source.Id]),
                    LocalSizeBytes = fixture.OriginalBytes[source.Id].Length,
                    ConversionContract = ClientReleaseArchiveAdapter.Contract,
                    State = ClientReleaseImportAssetState.Imported, UpdatedAtUtc = now
                }]
            });
            await seed.SaveChangesAsync();
        }

        var repoDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoDirectory.Parent is not null &&
               !Directory.Exists(Path.Combine(repoDirectory.FullName, "tools")))
            repoDirectory = repoDirectory.Parent;
        var probe = Path.Combine(repoDirectory.FullName, "tools", "NetRatel.ClientArtifactCrashProbe",
            "bin", "Debug", "net10.0", "NetRatel.ClientArtifactCrashProbe.dll");
        Assert.True(File.Exists(probe), $"Crash probe is missing: {probe}");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            _postgres.GetConnectionString(), _storage, operationId.ToString(), leaseOwner.ToString(), "1",
            source.RuntimeId, fixture.Release.Version, archivePath, fixture.Release.Tag, source.Id.ToString(),
            source.Name, source.Sha256Digest![7..], fixture.Commit, ClientReleaseArchiveAdapter.Contract
        }) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Insert(0, probe);

        using var child = Process.Start(startInfo)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await child.WaitForExitAsync(timeout.Token);
        var childOutput = await output;
        var childError = await error;
        Assert.NotEqual(0, child.ExitCode);

        var versionDirectory = Path.Combine(_storage, source.RuntimeId, fixture.Release.Version);
        var metadataPath = Path.Combine(versionDirectory, "metadata.json");
        Assert.True(File.Exists(metadataPath), $"Crash probe output: {childOutput}\n{childError}");
        Assert.True(File.Exists(Path.Combine(versionDirectory, $"NetRatel.Client-{source.RuntimeId}-{fixture.Release.Version}.zip")));

        await using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()))
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<IOptions<ClientArtifactsOptions>>(Options.Create(new ClientArtifactsOptions
            {
                StorageRoot = _storage, EnableFallbackScan = false
            }))
            .AddSingleton<IWebHostEnvironment>(new FixtureEnvironment(_storage))
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IOptions<AgentAuthOptions>>(Options.Create(new AgentAuthOptions()))
            .AddScoped<IEnrollmentCodeIssueService, EnrollmentCodeIssueService>()
            .AddSingleton<IArtifactZipInjectionService, ZipInjectionService>()
            .AddSingleton<ITenantLookupService>(new FixtureTenantLookup())
            .AddSingleton<IEventRecorder>(new NoopEventRecorder())
            .AddSingleton<ICorrelationContext>(new FixtureCorrelationContext())
            .AddScoped<IClientArtifactsService, ClientArtifactsService>()
            .BuildServiceProvider();

        await using (var scope = services.CreateAsyncScope())
        {
            var artifacts = scope.ServiceProvider.GetRequiredService<IClientArtifactsService>();
            Assert.Null(await artifacts.GetMetadataAsync(source.RuntimeId, fixture.Release.Version, CancellationToken.None));
            File.Delete(metadataPath);

            var conflictingPath = Path.Combine(_storage, "conflicting.zip");
            await File.WriteAllBytesAsync(conflictingPath, MakeArchive(source.RuntimeId, fixture.Release.Version,
                new string('c', 40)));
            var conflictingHash = Hash(await File.ReadAllBytesAsync(conflictingPath));
            await using (var conflicting = File.OpenRead(conflictingPath))
            {
                var conflictingFile = new FormFile(conflicting, 0, conflicting.Length, "file", source.Name)
                { Headers = new HeaderDictionary(), ContentType = "application/zip" };
                await Assert.ThrowsAsync<ClientArtifactConflictException>(() => artifacts.ImportVerifiedAsync(
                    conflictingFile, source.RuntimeId, fixture.Release.Version,
                    new ClientArtifactImportProvenance(operationId, "BostonTechnologies/netratel", fixture.Release.Tag,
                        source.Id, source.Name, conflictingHash, fixture.Commit,
                        ClientReleaseArchiveAdapter.Contract, leaseOwner, 1), "crash-probe", CancellationToken.None));
            }

            await using var original = File.OpenRead(archivePath);
            var originalFile = new FormFile(original, 0, original.Length, "file", source.Name)
            { Headers = new HeaderDictionary(), ContentType = "application/zip" };
            await artifacts.ImportVerifiedAsync(originalFile, source.RuntimeId, fixture.Release.Version,
                new ClientArtifactImportProvenance(operationId, "BostonTechnologies/netratel", fixture.Release.Tag,
                    source.Id, source.Name, source.Sha256Digest![7..], fixture.Commit,
                    ClientReleaseArchiveAdapter.Contract, leaseOwner, 1), "crash-probe", CancellationToken.None);
            await artifacts.CompleteImportVisibilityAsync(operationId,
                new ClientReleaseImportClaim(operationId, leaseOwner, 1), CancellationToken.None);
            Assert.NotNull(await artifacts.GetMetadataAsync(source.RuntimeId, fixture.Release.Version, CancellationToken.None));

            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.ClientReleaseImportOperations.Where(x => x.Id == operationId &&
                    x.LeaseOwner == leaseOwner && x.LeaseGeneration == 1)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.State, ClientReleaseImportState.Imported)
                    .SetProperty(x => x.ImportedAtUtc, now)
                    .SetProperty(x => x.LeaseOwner, (Guid?)null)
                    .SetProperty(x => x.LeaseUntilUtc, (DateTimeOffset?)null));
        }

        await using var verify = new OrchestratorDbContext(_dbOptions);
        Assert.Equal(ClientReleaseImportState.Imported,
            (await verify.ClientReleaseImportOperations.SingleAsync(x => x.Id == operationId)).State);
    }

    private static async Task WaitForStateAsync(IServiceProvider services, Guid id, ClientReleaseImportState state, CancellationToken ct)
    {
        using var observation = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await observation.WaitForNextTickAsync(ct))
        {
            await using var scope = services.CreateAsyncScope();
            var current = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
                .ClientReleaseImportOperations.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            if (current.State == state) return;
            Assert.NotEqual(ClientReleaseImportState.Failed, current.State);
        }
    }

    [Fact]
    [Trait("category", "hosted")]
    public async Task PublishedClientPackImportsEveryVerifiedRuntimeWithoutPublishing()
    {
        var fixtureDirectory = Environment.GetEnvironmentVariable("NETRATEL_RELEASE_FIXTURE_DIR")
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fixtureDirectory) || !Directory.Exists(fixtureDirectory))
            Assert.Skip("NETRATEL_RELEASE_FIXTURE_DIR is required for the hosted public-release fixture.");
        using var publication = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(fixtureDirectory, "publication.json")));
        var root = publication.RootElement;
        var version = root.GetProperty("productVersion").GetString()!;
        var commit = root.GetProperty("publicCommit").GetString()!;
        var pattern = new Regex($"^netratel-client-{Regex.Escape(version)}-(?<rid>[a-z0-9-]+)\\.(?:zip|tar\\.gz)$",
            RegexOptions.CultureInvariant);
        var sourcePaths = new Dictionary<long, string>();
        var assets = new List<GitHubClientAsset>();
        long assetId = 100;
        foreach (var file in root.GetProperty("inputReceipt").GetProperty("files").EnumerateObject())
        {
            var match = pattern.Match(file.Name);
            if (!match.Success) continue;
            var path = Path.Combine(fixtureDirectory, file.Name);
            Assert.True(File.Exists(path), $"Published client asset is missing: {file.Name}");
            sourcePaths.Add(++assetId, path);
            assets.Add(new GitHubClientAsset(assetId, file.Name, match.Groups["rid"].Value,
                new FileInfo(path).Length, $"sha256:{file.Value.GetProperty("sha256").GetString()}"));
        }
        Assert.NotEmpty(assets);
        var publicationPath = Path.Combine(fixtureDirectory, "publication.json");
        var checksumsPath = Path.Combine(fixtureDirectory, "SHA256SUMS");
        sourcePaths.Add(20, publicationPath);
        sourcePaths.Add(21, checksumsPath);
        var release = new GitHubClientRelease(1, $"v{version}", version, version,
            DateTimeOffset.UtcNow, version.Contains('-', StringComparison.Ordinal),
            $"https://github.com/BostonTechnologies/netratel/releases/tag/v{version}",
            assets, assets.Sum(x => x.SizeBytes), "verification required")
        {
            PublicationAsset = new GitHubReleaseEvidenceAsset(20, "publication.json",
                new FileInfo(publicationPath).Length, "sha256:" + await HashFileAsync(publicationPath)),
            ChecksumsAsset = new GitHubReleaseEvidenceAsset(21, "SHA256SUMS",
                new FileInfo(checksumsPath).Length, "sha256:" + await HashFileAsync(checksumsPath))
        };
        await using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()))
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<IGitHubClientReleaseCatalog>(new FixtureCatalog(release, commit))
            .AddScoped<IClientArtifactsService, ClientArtifactsService>()
            .AddScoped<IEnrollmentCodeIssueService, EnrollmentCodeIssueService>()
            .AddSingleton<IArtifactZipInjectionService, ZipInjectionService>()
            .AddSingleton<ITenantLookupService>(new FixtureTenantLookup())
            .AddSingleton<IEventRecorder>(new NoopEventRecorder())
            .AddSingleton<ICorrelationContext>(new FixtureCorrelationContext())
            .AddSingleton<IWebHostEnvironment>(new FixtureEnvironment(_storage))
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IOptions<AgentAuthOptions>>(Options.Create(new AgentAuthOptions()))
            .AddSingleton<IOptions<ClientArtifactsOptions>>(Options.Create(new ClientArtifactsOptions { StorageRoot = _storage }))
            .AddScoped<GitHubClientAssetDownloader>(_ => new GitHubClientAssetDownloader(
                new HttpClient(new FileAssetHandler(sourcePaths)) { Timeout = Timeout.InfiniteTimeSpan },
                new ConfigurationBuilder().Build()))
            .BuildServiceProvider();
        var id = Guid.NewGuid();
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.ClientReleaseImportOperations.Add(new ClientReleaseImportOperation
            {
                Id = id, GitHubReleaseId = release.Id, Tag = release.Tag,
                Version = version, RequestedBy = "hosted-fixture",
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var worker = ActivatorUtilities.CreateInstance<ClientReleaseImportWorker>(services);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var observation = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            ClientReleaseImportOperation operation;
            do
            {
                await observation.WaitForNextTickAsync(timeout.Token);
                await using var scope = services.CreateAsyncScope();
                operation = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
                    .ClientReleaseImportOperations.AsNoTracking().Include(x => x.Assets)
                    .SingleAsync(x => x.Id == id, timeout.Token);
            } while (operation.State is not (ClientReleaseImportState.Imported or ClientReleaseImportState.Failed));

            Assert.True(operation.State == ClientReleaseImportState.Imported, operation.Error);
            await using var inspect = services.CreateAsyncScope();
            var storedArtifacts = inspect.ServiceProvider.GetRequiredService<IClientArtifactsService>();
            var visible = await storedArtifacts.ListAsync(null, 0, 20, timeout.Token);
            Assert.Equal(assets.Count, visible.Items.Count);
            foreach (var asset in operation.Assets)
            {
                Assert.Equal(ClientReleaseImportAssetState.Imported, asset.State);
                var metadata = await storedArtifacts.GetMetadataAsync(asset.RuntimeId, version, timeout.Token);
                Assert.NotNull(metadata);
                Assert.Equal(asset.LocalSha256, metadata.Sha256);
                Assert.Equal(asset.LocalSizeBytes, metadata.Size);
                var download = await storedArtifacts.DownloadRawAsync(asset.RuntimeId, version, timeout.Token);
                await using var archive = download.Content;
                Assert.Equal(asset.LocalSha256,
                    Convert.ToHexString(await SHA256.HashDataAsync(archive, timeout.Token)).ToLowerInvariant());
                Assert.Equal(assets.Single(x => x.RuntimeId == asset.RuntimeId).Sha256Digest,
                    "sha256:" + asset.SourceSha256);
                using var metadataFile = JsonDocument.Parse(await File.ReadAllTextAsync(
                    Path.Combine(_storage, asset.RuntimeId, version, "metadata.json"), timeout.Token));
                var provenance = metadataFile.RootElement;
                Assert.Equal("BostonTechnologies/netratel", provenance.GetProperty("sourceRepository").GetString());
                Assert.Equal(release.Tag, provenance.GetProperty("sourceTag").GetString());
                Assert.Equal(asset.SourceName, provenance.GetProperty("sourceAssetName").GetString());
                Assert.Equal(asset.SourceSha256, provenance.GetProperty("sourceSha256").GetString());
                Assert.Equal(commit, provenance.GetProperty("sourceCommit").GetString());
                Assert.Equal(ClientReleaseArchiveAdapter.Contract,
                    provenance.GetProperty("importAdapterContract").GetString());
            }
            await using var verify = new OrchestratorDbContext(_dbOptions);
            Assert.Empty(await verify.ClientUpdateReleases.ToListAsync());
            Assert.Null(operation.PublishedAtUtc);
        }
        finally { await worker.StopAsync(CancellationToken.None); worker.Dispose(); }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static Fixture CreateFixture(bool corruptSecondRuntime)
    {
        const string version = "1.2.3";
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var source = new Dictionary<long, byte[]>();
        var original = new Dictionary<long, byte[]>();
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
            original[id] = bytes;
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
        return new Fixture(release, commit, source, original);
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

    private sealed record Fixture(GitHubClientRelease Release, string Commit,
        Dictionary<long, byte[]> Bytes, Dictionary<long, byte[]> OriginalBytes);

    private sealed class FixtureCatalog(GitHubClientRelease release, string commit) : IGitHubClientReleaseCatalog
    {
        public Task<GitHubClientReleasePage> ListAsync(string channel, int page, bool refresh, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<GitHubClientRelease?> FindAsync(long releaseId, CancellationToken cancellationToken) =>
            Task.FromResult<GitHubClientRelease?>(releaseId == release.Id ? release : null);
        public Task<string> ResolveTagCommitAsync(string tag, CancellationToken cancellationToken) =>
            Task.FromResult(commit);
    }

    private sealed class AssetHandler(Dictionary<long, byte[]> bytes, Action<long>? onRequest = null,
        Func<long, CancellationToken, Task>? wait = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var id = long.Parse(request.RequestUri!.Segments[^1]);
            onRequest?.Invoke(id);
            if (wait is not null) await wait(id, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes[id])
            };
        }
    }

    private sealed class FileAssetHandler(IReadOnlyDictionary<long, string> paths) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var id = long.Parse(request.RequestUri!.Segments[^1]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(File.OpenRead(paths[id]))
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

    private sealed class FixtureTenantLookup : ITenantLookupService
    {
        public Task<bool> TenantExistsAsync(int tenantId, CancellationToken ct = default) =>
            Task.FromResult(tenantId > 0);
    }

    private sealed class NoopEventRecorder : IEventRecorder
    {
        public Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixtureCorrelationContext : ICorrelationContext
    {
        public string? Current => "published-client-pack";
        public string GetOrCreate() => Current!;
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private long _utcTicks = initial.UtcDateTime.Ticks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }

    private sealed class ImportOwnershipFenceInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _releaseFirstFence =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pausedFirstFence;

        public TaskCompletionSource<CommandObservation> FirstFenceStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CommandObservation> StaleFenceRejected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<CommandObservation> Commands { get; } = [];

        public void ReleaseFirstFence() => _releaseFirstFence.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var observation = Observe(command);
            if (observation.IsOwnershipFence && Interlocked.Exchange(ref _pausedFirstFence, 1) == 0)
            {
                FirstFenceStarted.TrySetResult(observation);
                await _releaseFirstFence.Task.WaitAsync(cancellationToken);
            }

            return result;
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Observe(command);
            return result;
        }

        public override int NonQueryExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result)
        {
            ObserveFenceOutcome(command, result);
            return base.NonQueryExecuted(command, eventData, result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            ObserveFenceOutcome(command, result);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Observe(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private CommandObservation Observe(DbCommand command)
        {
            var observation = new CommandObservation(
                command.CommandText,
                command.Transaction is not null,
                IsOwnershipFence(command),
                IsAssetInsert(command));
            Commands.Enqueue(observation);
            return observation;
        }

        private void ObserveFenceOutcome(DbCommand command, int result)
        {
            if (result == 0 && IsOwnershipFence(command))
            {
                StaleFenceRejected.TrySetResult(new CommandObservation(
                    command.CommandText,
                    command.Transaction is not null,
                    true,
                    false));
            }
        }

        private static bool IsOwnershipFence(DbCommand command) =>
            command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) &&
            command.CommandText.Contains("ClientReleaseImportOperations", StringComparison.Ordinal) &&
            command.CommandText.Split("\"UpdatedAtUtc\"", StringSplitOptions.None).Length > 2;

        private static bool IsAssetInsert(DbCommand command) =>
            command.CommandText.Contains("ClientReleaseImportAssets", StringComparison.Ordinal) &&
            command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase);

        public sealed record CommandObservation(
            string Text,
            bool HasTransaction,
            bool IsOwnershipFence,
            bool IsAssetInsert);
    }

    private sealed class RecordingArtifactStore : IClientArtifactsService
    {
        public List<string> ImportedRuntimes { get; } = [];
        public Dictionary<string, (long Size, string Sha256)> Stored { get; } = [];
        public bool Visible { get; private set; }
        public async Task<ClientArtifactUploadResultDto> ImportVerifiedAsync(IFormFile file, string rid, string version,
            ClientArtifactImportProvenance provenance, string? importedBy, CancellationToken ct)
        {
            using var stream = file.OpenReadStream();
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            Stored.Add(rid, (file.Length, sha256));
            ImportedRuntimes.Add(rid);
            return new ClientArtifactUploadResultDto();
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

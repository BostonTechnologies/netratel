using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Models;
using NetRatel.API.Services;
using NetRatel.Akka.Configuration;
using NetRatel.Infrastructure.Persistence;
using NuGet.Versioning;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientReleaseAutomationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private DbContextOptions<OrchestratorDbContext> _options = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options;
        await using var db = new OrchestratorDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Fact]
    public async Task PolicyIsOffUntilSavedAndSchedulesFromUtcWithARevisionGuard()
    {
        var now = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        await using var db = new OrchestratorDbContext(_options);
        var service = new ClientReleaseAutomationService(db, clock);
        var initial = await service.GetAsync(CancellationToken.None);
        Assert.Equal(0, initial.CheckEveryHours);
        Assert.Null(initial.NextCheckAtUtc);
        Assert.False(initial.PublishAutomatically);
        Assert.False(initial.DownloadPrerelease);

        var daily = await service.UpdateAsync(new(24, true, false, false, false, initial.Revision),
            "admin-a", CancellationToken.None);
        Assert.InRange(daily.NextCheckAtUtc!.Value, now.AddHours(24), now.AddHours(24).AddMinutes(5));
        Assert.Equal("admin-a", daily.UpdatedBy);
        Assert.Equal(1, daily.Revision);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.UpdateAsync(
            new(12, true, false, false, false, initial.Revision), "admin-b", CancellationToken.None));

        var twiceDaily = await service.UpdateAsync(new(12, true, false, false, false, daily.Revision),
            "admin-a", CancellationToken.None);
        Assert.InRange(twiceDaily.NextCheckAtUtc!.Value, now.AddHours(12), now.AddHours(12).AddMinutes(5));
        var due = await service.CheckNowAsync("admin-a", CancellationToken.None);
        Assert.Equal(now, due.NextCheckAtUtc);
        Assert.Equal(twiceDaily.Revision + 1, due.Revision);
        var disabled = await service.UpdateAsync(new(0, false, false, false, false, due.Revision),
            "admin-a", CancellationToken.None);
        Assert.Null(disabled.NextCheckAtUtc);
    }

    [Fact]
    public async Task AutomaticPrereleaseDeploymentRequiresBothOtherOptIns()
    {
        await using var db = new OrchestratorDbContext(_options);
        var service = new ClientReleaseAutomationService(db, new FixedClock(DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(
            new(12, true, true, false, true, 0), "admin", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(
            new(12, true, false, true, true, 0), "admin", CancellationToken.None));
        var policy = await service.GetAsync(CancellationToken.None);
        Assert.Equal(0, policy.Revision);
    }

    [Fact]
    public void NewestUsesSemverAndKeepsChannelsSeparate()
    {
        var releases = new[]
        {
            Release(1, "1.2.0-rc.9", true),
            Release(2, "1.2.0-rc.10", true),
            Release(3, "1.2.0", false),
            Release(4, "1.1.9", false)
        };
        Assert.Equal(2, ClientReleaseAutomationWorker.Newest(releases, prerelease: true)!.Id);
        Assert.Equal(3, ClientReleaseAutomationWorker.Newest(releases, prerelease: false)!.Id);
        Assert.Throws<InvalidDataException>(() => ClientReleaseAutomationWorker.Newest(
            [Release(5, "1.2.1-rc.1", false)], prerelease: false));
    }

    [Fact]
    public void AutomaticImportRequiresEveryRuntimeToBeCoveredWithinItsOwnChannel()
    {
        var runtimes = new[] { "linux-x64", "win-x64" };
        var candidate = NuGetVersion.Parse("1.2.0");
        var offered = new (string RuntimeId, string Channel, string Version)[]
        {
            ("linux-x64", "prerelease", "1.3.0-rc.1"),
            ("win-x64", "stable", "1.2.0")
        };
        Assert.False(ClientReleaseAutomationWorker.AllRuntimesCovered(runtimes, offered, "stable", candidate));

        offered = [.. offered, ("linux-x64", "stable", "1.2.1")];
        Assert.True(ClientReleaseAutomationWorker.AllRuntimesCovered(runtimes, offered, "stable", candidate));
        Assert.False(ClientReleaseAutomationWorker.AllRuntimesCovered(runtimes, offered, "prerelease",
            NuGetVersion.Parse("1.3.0-rc.1")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoWorkersClaimOnlyOneDueCheckAndPersistFailureForRetry(bool recoverStaleLease)
    {
        var now = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        await using (var db = new OrchestratorDbContext(_options))
        {
            var policy = new ClientReleaseAutomationService(db, clock);
            await policy.UpdateAsync(new(12, true, false, false, false, 0), "admin", CancellationToken.None);
            await policy.CheckNowAsync("admin", CancellationToken.None);
            if (recoverStaleLease)
            {
                await db.ClientReleaseAutomationSettings.ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.LeaseOwner, Guid.NewGuid())
                    .SetProperty(x => x.LeaseUntilUtc, now.AddSeconds(-1)));
            }
        }
        var catalog = new FailingCatalog();
        await using var services = new ServiceCollection()
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()))
            .AddSingleton<IGitHubClientReleaseCatalog>(catalog)
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var first = new ClientReleaseAutomationWorker(scopes, clock,
            NullLogger<ClientReleaseAutomationWorker>.Instance);
        var second = new ClientReleaseAutomationWorker(scopes, clock,
            NullLogger<ClientReleaseAutomationWorker>.Instance);
        await Task.WhenAll(first.RunCheckIfDueAsync(CancellationToken.None),
            second.RunCheckIfDueAsync(CancellationToken.None));
        Assert.Equal(1, catalog.CallCount);
        await using var verify = new OrchestratorDbContext(_options);
        var stored = await verify.ClientReleaseAutomationSettings.SingleAsync();
        Assert.Equal(now, stored.LastAttemptAtUtc);
        Assert.Null(stored.LastSuccessAtUtc);
        Assert.Equal("fixture catalogue unavailable", stored.LastError);
        Assert.Equal(now.AddMinutes(15), stored.NextCheckAtUtc);
        Assert.Null(stored.LeaseOwner);
        Assert.Null(stored.LeaseUntilUtc);
    }

    [Fact]
    public async Task AutomaticPublicationRechecksPrereleasePolicyAfterImportCompletes()
    {
        var now = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var id = Guid.NewGuid();
        const string version = "1.2.3-rc.1";
        var hash = new string('a', 64);
        await using (var db = new OrchestratorDbContext(_options))
        {
            var policy = new ClientReleaseAutomationService(db, clock);
            var enabled = await policy.UpdateAsync(new(12, true, true, true, true, 0),
                "fixture-admin", CancellationToken.None);
            db.ClientReleaseImportOperations.Add(new ClientReleaseImportOperation
            {
                Id = id, GitHubReleaseId = 123, Tag = "v" + version,
                Version = version, BuildCommit = new string('b', 40),
                RequestedBy = "client-release-automation", IsAutomatic = true,
                State = ClientReleaseImportState.Imported, CreatedAtUtc = now, UpdatedAtUtc = now,
                ImportedAtUtc = now,
                Assets = [new ClientReleaseImportAsset
                {
                    RuntimeId = "linux-x64", GitHubAssetId = 321,
                    SourceName = "fixture.zip", SourceSha256 = hash, SourceSizeBytes = 1,
                    LocalSha256 = hash, LocalSizeBytes = 1,
                    State = ClientReleaseImportAssetState.Imported, UpdatedAtUtc = now
                }]
            });
            await db.SaveChangesAsync();
            await policy.UpdateAsync(new(12, true, true, true, false, enabled.Revision),
                "fixture-admin", CancellationToken.None);
        }

        await using var services = new ServiceCollection()
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()))
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var dbForPublish = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var options = new NetRatelAkkaMigrationOptions
        {
            Enabled = true, PresenceAuthorityEnabled = true,
            GatewayEnabled = true, ClientUpdatesEnabled = true
        };
        var catalog = new ClientUpdateCatalog(services.GetRequiredService<IServiceScopeFactory>(), clock);
        var authority = new ClientUpdateAuthorityService(dbForPublish, catalog, options,
            clock, NullLogger<ClientUpdateAuthorityService>.Instance);
        var item = new ClientPackPublishItem(new ClientArtifactSummaryDto
        {
            Rid = "linux-x64", Version = version, FileName = "fixture.zip",
            Sha256 = hash, Size = 1
        }, "{}");

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.PublishImportedPackAsync(id, [item], "client-release-automation",
                confirmPrerelease: true, cancellationToken: CancellationToken.None, automatic: true));
        Assert.Equal("Automatic client publication is disabled by current policy.", rejected.Message);
        Assert.Empty(await dbForPublish.ClientUpdateReleases.ToListAsync());
        Assert.Null((await dbForPublish.ClientReleaseImportOperations.SingleAsync(x => x.Id == id)).PublishedAtUtc);
    }

    [Fact]
    public async Task AutomaticPublicationSkipsIneligibleNewerImportAndPublishesEligibleStableImport()
    {
        var now = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var artifacts = new PublicationArtifacts();
        var stable = AddImportedOperation(artifacts, "1.2.0", commit, now, createdAt: now.AddMinutes(-2));
        var preview = AddImportedOperation(artifacts, "1.3.0-rc.1", commit, now, createdAt: now.AddMinutes(-1));

        await using (var db = new OrchestratorDbContext(_options))
        {
            db.ClientReleaseAutomationSettings.Add(new ClientReleaseAutomationSettings
            {
                Id = 1,
                CheckEveryHours = 12,
                DownloadStable = true,
                DownloadPrerelease = true,
                PublishAutomatically = true,
                DeployPrereleaseAutomatically = false,
                NextCheckAtUtc = now,
                UpdatedBy = "fixture-admin",
                UpdatedAtUtc = now
            });
            db.ClientReleaseImportOperations.AddRange(stable, preview);
            await db.SaveChangesAsync();
        }

        await using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()))
            .AddSingleton(clock)
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton(new NetRatelAkkaMigrationOptions
            {
                Enabled = true,
                PresenceAuthorityEnabled = true,
                GatewayEnabled = true,
                ClientUpdatesEnabled = true
            })
            .AddSingleton<ClientUpdateCatalog>()
            .AddSingleton<IClientUpdateCatalog>(provider => provider.GetRequiredService<ClientUpdateCatalog>())
            .AddSingleton<IGitHubClientReleaseCatalog>(new EmptyReleaseCatalog())
            .AddSingleton<IClientArtifactsService>(artifacts)
            .AddScoped<ClientUpdateAuthorityService>()
            .AddScoped<ClientReleaseImportService>()
            .BuildServiceProvider();

        var worker = new ClientReleaseAutomationWorker(
            services.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<ClientReleaseAutomationWorker>.Instance);
        await worker.PublishImportedIfEligibleAsync(CancellationToken.None);

        await using var verify = new OrchestratorDbContext(_options);
        var storedStable = await verify.ClientReleaseImportOperations.SingleAsync(x => x.Id == stable.Id);
        var storedPreview = await verify.ClientReleaseImportOperations.SingleAsync(x => x.Id == preview.Id);
        Assert.NotNull(storedStable.PublishedAtUtc);
        Assert.Null(storedPreview.PublishedAtUtc);
        var published = await verify.ClientUpdateReleases.ToListAsync();
        Assert.Single(published);
        Assert.Equal("1.2.0", published[0].Version);
        Assert.Equal("stable", published[0].Channel);
    }

    [Fact]
    public async Task AutomaticStablePublicationIgnoresHigherPreviewAndKeepsClientOffersChannelAware()
    {
        var now = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        const string commit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var item = CreatePublishItem("linux-x64", "1.2.0", commit);
        var operationId = Guid.NewGuid();
        var previewId = Guid.NewGuid();
        var stableTenantAgent = Guid.NewGuid();
        var previewTenantAgent = Guid.NewGuid();

        await using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()))
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton(new NetRatelAkkaMigrationOptions
            {
                Enabled = true,
                PresenceAuthorityEnabled = true,
                GatewayEnabled = true,
                ClientUpdatesEnabled = true
            })
            .AddSingleton<ClientUpdateCatalog>()
            .AddSingleton<IClientUpdateCatalog>(provider => provider.GetRequiredService<ClientUpdateCatalog>())
            .AddScoped<ClientUpdateAuthorityService>()
            .BuildServiceProvider();

        await using (var seed = new OrchestratorDbContext(_options))
        {
            seed.ClientReleaseAutomationSettings.Add(new ClientReleaseAutomationSettings
            {
                Id = 1, CheckEveryHours = 12, DownloadStable = true, DownloadPrerelease = true,
                PublishAutomatically = true, DeployPrereleaseAutomatically = true,
                NextCheckAtUtc = now, UpdatedBy = "fixture-admin", UpdatedAtUtc = now
            });
            seed.Tenants.AddRange(
                new Tenant { Id = 1, Name = "stable", AutoUpdate = true, AutoUpdateChannel = "stable", CreatedAtUtc = now, UpdatedAtUtc = now },
                new Tenant { Id = 2, Name = "preview", AutoUpdate = true, AutoUpdateChannel = "prerelease", CreatedAtUtc = now, UpdatedAtUtc = now });
            seed.Agents.AddRange(
                new Agent { Id = stableTenantAgent, TenantId = 1, Status = AgentStatus.Active, CreatedAtUtc = now },
                new Agent { Id = previewTenantAgent, TenantId = 2, Status = AgentStatus.Active, CreatedAtUtc = now });
            seed.ClientReleaseImportOperations.Add(CreateImportedOperation(
                operationId, "1.2.0", commit, item.Artifact, now));
            seed.ClientUpdateReleases.Add(new ClientUpdateReleaseRecord
            {
                PublicId = previewId, Revision = 0, RuntimeId = "linux-x64", Version = "1.3.0-rc.1",
                Channel = "prerelease", ArtifactKey = "linux-x64/1.3.0-rc.1/preview.zip",
                Sha256 = new string('c', 64), SizeBytes = 1, ManifestJson = "{}", Enabled = true,
                PublishedAtUtc = now
            });
            await seed.SaveChangesAsync();
        }

        await using (var scope = services.CreateAsyncScope())
        {
            var authority = scope.ServiceProvider.GetRequiredService<ClientUpdateAuthorityService>();
            await authority.PublishImportedPackAsync(operationId, [item], "client-release-automation",
                confirmPrerelease: false, CancellationToken.None, automatic: true);

            var catalog = scope.ServiceProvider.GetRequiredService<ClientUpdateCatalog>();
            var stableOffer = catalog.GetOffer(1, stableTenantAgent, "linux-x64", "1.1.9", "prerelease");
            var previewOffer = catalog.GetOffer(2, previewTenantAgent, "linux-x64", "1.2.5-rc.1", "stable");
            Assert.NotNull(stableOffer);
            Assert.Equal("1.2.0", stableOffer!.Version);
            Assert.NotNull(previewOffer);
            Assert.Equal("1.3.0-rc.1", previewOffer!.Version);
        }
    }

    [Fact]
    public async Task AutomaticPublicationCompletesMissingRuntimeWhenEqualEntryAlreadyMatches()
    {
        var now = DateTimeOffset.UtcNow;
        const string commit = "cccccccccccccccccccccccccccccccccccccccc";
        var linux = CreatePublishItem("linux-x64", "1.2.0", commit);
        var windows = CreatePublishItem("win-x64", "1.2.0", commit);
        var operationId = Guid.NewGuid();
        await SeedAutomaticPublicationAsync(now, operationId, "1.2.0", commit, [linux, windows],
            new ClientUpdateReleaseRecord
            {
                PublicId = Guid.NewGuid(), RuntimeId = linux.Artifact.Rid, Version = linux.Artifact.Version,
                Channel = "stable", ArtifactKey = "existing-linux", Sha256 = linux.Artifact.Sha256,
                SizeBytes = linux.Artifact.Size, ManifestJson = linux.ManifestJson, Enabled = true,
                PublishedAtUtc = now
            });

        await using var db = new OrchestratorDbContext(_options);
        var authority = CreateAuthority(db, new FixedClock(now));
        await authority.PublishImportedPackAsync(operationId, [linux, windows], "fixture-admin",
            confirmPrerelease: false, CancellationToken.None, automatic: true);
        Assert.Equal(2, await db.ClientUpdateReleases.CountAsync());
        Assert.Equal(2, await db.ClientUpdateReleases.CountAsync(x => x.Version == "1.2.0"));
    }

    [Fact]
    public async Task AutomaticPublicationDoesNotRegressRuntimeWithNewerStableOffer()
    {
        var now = DateTimeOffset.UtcNow;
        const string commit = "dddddddddddddddddddddddddddddddddddddddd";
        var item = CreatePublishItem("linux-x64", "1.2.0", commit);
        var newer = new ClientUpdateReleaseRecord
        {
            PublicId = Guid.NewGuid(), RuntimeId = item.Artifact.Rid, Version = "1.3.0",
            Channel = "stable", ArtifactKey = "newer", Sha256 = new string('d', 64),
            SizeBytes = 1, ManifestJson = "{}", Enabled = true, PublishedAtUtc = now
        };
        var operationId = Guid.NewGuid();
        await SeedAutomaticPublicationAsync(now, operationId, "1.2.0", commit, [item], newer);

        await using var db = new OrchestratorDbContext(_options);
        var authority = CreateAuthority(db, new FixedClock(now));
        await authority.PublishImportedPackAsync(operationId, [item], "fixture-admin",
            confirmPrerelease: false, CancellationToken.None, automatic: true);
        Assert.Single(await db.ClientUpdateReleases.ToListAsync());
        Assert.Equal("1.3.0", (await db.ClientUpdateReleases.SingleAsync()).Version);
    }

    [Fact]
    public async Task AutomaticPublicationRejectsDisabledConflictingEqualEntryAtomically()
    {
        var now = DateTimeOffset.UtcNow;
        const string commit = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var item = CreatePublishItem("linux-x64", "1.2.0", commit);
        var operationId = Guid.NewGuid();
        await SeedAutomaticPublicationAsync(now, operationId, "1.2.0", commit, [item],
            new ClientUpdateReleaseRecord
            {
                PublicId = Guid.NewGuid(), RuntimeId = item.Artifact.Rid, Version = item.Artifact.Version,
                Channel = "stable", ArtifactKey = "disabled-conflict", Sha256 = new string('f', 64),
                SizeBytes = 1, ManifestJson = "{}", Enabled = false, PublishedAtUtc = now
            });

        await using var db = new OrchestratorDbContext(_options);
        var authority = CreateAuthority(db, new FixedClock(now));
        await Assert.ThrowsAsync<ClientArtifactConflictException>(() => authority.PublishImportedPackAsync(
            operationId, [item], "fixture-admin", confirmPrerelease: false,
            CancellationToken.None, automatic: true));
        Assert.Single(await db.ClientUpdateReleases.ToListAsync());
        Assert.Null((await db.ClientReleaseImportOperations.SingleAsync(x => x.Id == operationId)).PublishedAtUtc);
    }

    private static ClientPackPublishItem CreatePublishItem(string rid, string version, string commit)
    {
        var bytes = MakeArchive(rid, version, commit);
        return new ClientPackPublishItem(new ClientArtifactSummaryDto
        {
            Rid = rid, Version = version, FileName = $"NetRatel.Client-{rid}-{version}.zip",
            Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        }, JsonSerializer.Serialize(new { schema = "netratel.client.manifest.v1", product = "NetRatel.Client", version, runtimeId = rid, commitSha = commit, executable = "NetRatel.Client" }));
    }

    private static ClientReleaseImportOperation CreateImportedOperation(Guid id, string version, string commit,
        ClientArtifactSummaryDto artifact, DateTimeOffset now)
        => new()
        {
            Id = id, GitHubReleaseId = Random.Shared.NextInt64(1, long.MaxValue), Tag = "v" + version,
            Version = version, BuildCommit = commit, RequestedBy = "fixture-admin",
            IsAutomatic = true, State = ClientReleaseImportState.Imported,
            CreatedAtUtc = now, UpdatedAtUtc = now, ImportedAtUtc = now,
            Assets =
            [new ClientReleaseImportAsset
            {
                RuntimeId = artifact.Rid, GitHubAssetId = Random.Shared.NextInt64(1, long.MaxValue),
                SourceName = artifact.FileName, SourceSha256 = artifact.Sha256,
                SourceSizeBytes = artifact.Size, LocalSha256 = artifact.Sha256, LocalSizeBytes = artifact.Size,
                State = ClientReleaseImportAssetState.Imported, UpdatedAtUtc = now
            }]
        };

    private async Task SeedAutomaticPublicationAsync(DateTimeOffset now, Guid operationId, string version,
        string commit, IReadOnlyList<ClientPackPublishItem> items, params ClientUpdateReleaseRecord[] existing)
    {
        await using var db = new OrchestratorDbContext(_options);
        db.ClientReleaseAutomationSettings.Add(new ClientReleaseAutomationSettings
        {
            Id = 1, CheckEveryHours = 12, DownloadStable = true, DownloadPrerelease = true,
            PublishAutomatically = true, DeployPrereleaseAutomatically = true,
            NextCheckAtUtc = now, UpdatedBy = "fixture-admin", UpdatedAtUtc = now
        });
        var operation = CreateImportedOperation(operationId, version, commit, items[0].Artifact, now);
        db.ClientReleaseImportOperations.Add(operation);
        if (items.Count > 1)
        {
            foreach (var item in items.Skip(1))
            {
                operation.Assets.Add(new ClientReleaseImportAsset
                {
                    RuntimeId = item.Artifact.Rid, GitHubAssetId = Random.Shared.NextInt64(1, long.MaxValue),
                    SourceName = item.Artifact.FileName, SourceSha256 = item.Artifact.Sha256,
                    SourceSizeBytes = item.Artifact.Size, LocalSha256 = item.Artifact.Sha256,
                    LocalSizeBytes = item.Artifact.Size, State = ClientReleaseImportAssetState.Imported,
                    UpdatedAtUtc = now
                });
            }
        }
        db.ClientUpdateReleases.AddRange(existing);
        await db.SaveChangesAsync();
    }

    private ClientUpdateAuthorityService CreateAuthority(OrchestratorDbContext db, TimeProvider clock)
    {
        var catalog = new ClientUpdateCatalog(
            new ServiceCollection().AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()))
                .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), clock);
        return new ClientUpdateAuthorityService(db, catalog, new NetRatelAkkaMigrationOptions
        {
            Enabled = true, PresenceAuthorityEnabled = true, GatewayEnabled = true, ClientUpdatesEnabled = true
        }, clock, NullLogger<ClientUpdateAuthorityService>.Instance);
    }

    private static ClientReleaseImportOperation AddImportedOperation(
        PublicationArtifacts artifacts, string version, string commit, DateTimeOffset now,
        DateTimeOffset createdAt)
    {
        var rid = "linux-x64";
        var bytes = MakeArchive(rid, version, commit);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        artifacts.Add(rid, version, bytes, hash);
        return new ClientReleaseImportOperation
        {
            Id = Guid.NewGuid(),
            GitHubReleaseId = Random.Shared.NextInt64(1, long.MaxValue),
            Tag = "v" + version,
            Version = version,
            BuildCommit = commit,
            RequestedBy = "client-release-automation",
            IsAutomatic = true,
            State = ClientReleaseImportState.Imported,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = now,
            ImportedAtUtc = now,
            Assets =
            [
                new ClientReleaseImportAsset
                {
                    RuntimeId = rid,
                    GitHubAssetId = Random.Shared.NextInt64(1, long.MaxValue),
                    SourceName = $"netratel-client-{version}-{rid}.zip",
                    SourceSha256 = hash,
                    SourceSizeBytes = bytes.Length,
                    LocalSha256 = hash,
                    LocalSizeBytes = bytes.Length,
                    State = ClientReleaseImportAssetState.Imported,
                    UpdatedAtUtc = now
                }
            ]
        };
    }

    private static byte[] MakeArchive(string runtime, string version, string commit)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var prefix = $"netratel-client-{runtime}/";
            var executable = "NetRatel.Client";
            var manifest = archive.CreateEntry(prefix + "netratel-client-manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
            {
                writer.Write(JsonSerializer.Serialize(new
                {
                    schema = "netratel.client.manifest.v1", product = "NetRatel.Client",
                    version, runtimeId = runtime, commitSha = commit, executable
                }));
            }
            var binary = archive.CreateEntry(prefix + executable);
            using var content = new StreamWriter(binary.Open());
            content.Write("fixture executable bytes");
        }
        return buffer.ToArray();
    }

    private static GitHubClientRelease Release(long id, string version, bool prerelease) =>
        new(id, $"v{version}", version, version, DateTimeOffset.UtcNow, prerelease,
            "https://example.invalid/release", [], 0, "verification required");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailingCatalog : IGitHubClientReleaseCatalog
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public Task<GitHubClientReleasePage> ListAsync(string channel, int page, bool refresh, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            throw new InvalidDataException("fixture catalogue unavailable");
        }
        public Task<GitHubClientRelease?> FindAsync(long releaseId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<string> ResolveTagCommitAsync(string tag, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyReleaseCatalog : IGitHubClientReleaseCatalog
    {
        public Task<GitHubClientReleasePage> ListAsync(string channel, int page, bool refresh, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitHubClientRelease?> FindAsync(long releaseId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ResolveTagCommitAsync(string tag, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class PublicationArtifacts : IClientArtifactsService
    {
        private readonly Dictionary<(string Rid, string Version), (byte[] Bytes, string Sha256)> _artifacts = [];

        public void Add(string rid, string version, byte[] bytes, string sha256) =>
            _artifacts[(rid, version)] = (bytes, sha256);

        public Task<ClientArtifactSummaryDto?> GetMetadataAsync(string rid, string version, CancellationToken ct)
        {
            var artifact = _artifacts[(rid, version)];
            return Task.FromResult<ClientArtifactSummaryDto?>(new ClientArtifactSummaryDto
            {
                Rid = rid,
                Version = version,
                FileName = $"NetRatel.Client-{rid}-{version}.zip",
                Size = artifact.Bytes.Length,
                Sha256 = artifact.Sha256
            });
        }

        public Task<ClientArtifactDownloadResult> DownloadRawAsync(string rid, string versionOrLatest, CancellationToken ct)
        {
            var artifact = _artifacts[(rid, versionOrLatest)];
            return Task.FromResult(new ClientArtifactDownloadResult(
                new MemoryStream(artifact.Bytes, writable: false), "application/zip",
                $"NetRatel.Client-{rid}-{versionOrLatest}.zip", false, new ClientArtifactSummaryDto
                {
                    Rid = rid,
                    Version = versionOrLatest,
                    FileName = $"NetRatel.Client-{rid}-{versionOrLatest}.zip",
                    Size = artifact.Bytes.Length,
                    Sha256 = artifact.Sha256
                }));
        }

        public Task<ClientArtifactListDto> ListAsync(string? rid, int skip, int take, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactSummaryDto?> GetLatestAsync(string rid, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactUploadResultDto> UploadAsync(IFormFile file, string rid, string version, string? notes, string? uploadedBy, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> DownloadAsync(string rid, string versionOrLatest, bool allowFallback, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> DownloadForClientAsync(ClientDownloadRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string rid, string version, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> RunFallbackScanAsync(string rid, string? version, CancellationToken ct) => throw new NotSupportedException();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
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
    public async Task TwoWorkersClaimOnlyOneDueCheckAndPersistFailureForRetry()
    {
        var now = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        await using (var db = new OrchestratorDbContext(_options))
        {
            var policy = new ClientReleaseAutomationService(db, clock);
            await policy.UpdateAsync(new(12, true, false, false, false, 0), "admin", CancellationToken.None);
            await policy.CheckNowAsync("admin", CancellationToken.None);
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
}

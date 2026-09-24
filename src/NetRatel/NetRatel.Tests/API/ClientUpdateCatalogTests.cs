using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientUpdateCatalogTests
{
    [Fact]
    public async Task Snapshot_Provides_Bounded_Channel_Offers_Without_A_Database_On_Heartbeat()
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var databaseName = $"updates-{Guid.NewGuid():N}";
        var services = new ServiceCollection()
            .AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .BuildServiceProvider();
        var catalog = new ClientUpdateCatalog(services.GetRequiredService<IServiceScopeFactory>(), clock);

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Tenants.AddRange(
                new Tenant { Id = tenantId, Name = "Canary", AutoUpdate = true, AutoUpdateChannel = "prerelease", AutoUpdateTargetVersion = "0.4.103-rc.1" },
                new Tenant { Id = 74, Name = "Stable", AutoUpdate = true },
                new Tenant { Id = 75, Name = "Stable pinned to prerelease", AutoUpdate = true,
                    AutoUpdateChannel = "stable", AutoUpdateTargetVersion = "0.4.103-rc.1" });
            db.ClientUpdateCatalogRevisions.Add(new ClientUpdateCatalogRevision { Id = 1, Revision = 9, UpdatedAtUtc = now });
            db.ClientUpdateReleases.AddRange(
                Release("0.4.102", "stable", 8),
                Release("0.4.103-rc.1", "prerelease", 9));
            await db.SaveChangesAsync();
        }

        await catalog.RefreshAsync(CancellationToken.None);
        await services.DisposeAsync();

        var stable = catalog.GetOffer(tenantId, agentId, "linux-x64", "0.4.101", "stable");
        stable.Should().NotBeNull();
        stable!.Version.Should().Be("0.4.103-rc.1");
        catalog.GetOffer(74, Guid.NewGuid(), "linux-x64", "0.4.101", "stable")!.Version.Should().Be("0.4.102");
        catalog.GetOffer(74, Guid.NewGuid(), "linux-x64", "0.4.101", "prerelease")!.Version.Should().Be("0.4.102",
            "the agent cannot opt a stable tenant into prerelease deployment");
        catalog.GetOffer(75, Guid.NewGuid(), "linux-x64", "0.4.101", "stable").Should().BeNull(
            "a pinned target must not bypass the stable-only policy");
        var prerelease = catalog.GetOffer(tenantId, agentId, "linux-x64", "0.4.102-rc.1", "prerelease");
        prerelease.Should().NotBeNull();
        prerelease!.Version.Should().Be("0.4.103-rc.1");

        var offered = 0;
        for (var index = 0; index < 10_000; index++)
            if (catalog.GetOffer(tenantId, Guid.NewGuid(), "linux-x64", "0.4.101", "stable") is not null)
                offered++;
        offered.Should().Be(10_000);

        clock.Advance(TimeSpan.FromSeconds(91));
        catalog.GetOffer(tenantId, agentId, "linux-x64", "0.4.101", "stable").Should().BeNull();
    }

    [Fact]
    public async Task Snapshot_Enforces_Suspension_Suppression_And_Resume_Revision()
    {
        const int tenantId = 84;
        var agentId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var databaseName = $"updates-{Guid.NewGuid():N}";
        await using var services = new ServiceCollection()
            .AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .BuildServiceProvider();
        var catalog = new ClientUpdateCatalog(services.GetRequiredService<IServiceScopeFactory>(), new AdjustableTimeProvider(now));

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Suspended", AutoUpdate = true });
            db.Agents.Add(new Agent { Id = agentId, TenantId = tenantId });
            db.ClientUpdateCatalogRevisions.Add(new ClientUpdateCatalogRevision { Id = 1, Revision = 2, UpdatedAtUtc = now });
            var release = Release("0.4.102", "stable", 2);
            release.PublicId = releaseId;
            db.ClientUpdateReleases.Add(release);
            db.AgentClientUpdateStates.Add(new AgentClientUpdateStateRecord
            {
                AgentId = agentId,
                TenantId = tenantId,
                SuspendedAtUtc = now,
                SuppressedReleaseId = releaseId,
                PolicyRevision = 4
            });
            await db.SaveChangesAsync();
        }

        await catalog.RefreshAsync(CancellationToken.None);
        catalog.GetOffer(tenantId, agentId, "linux-x64", "0.4.101", "stable").Should().BeNull();
        catalog.GetPolicy(tenantId, agentId, 3).Should().Be(new ClientUpdatePolicySnapshot(4, true, true, false, releaseId));

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var state = await db.AgentClientUpdateStates.SingleAsync();
            state.SuspendedAtUtc = null;
            state.PolicyRevision = 5;
            await db.SaveChangesAsync();
        }
        await catalog.RefreshAsync(CancellationToken.None);

        catalog.GetPolicy(tenantId, agentId, 4).Should().Be(new ClientUpdatePolicySnapshot(5, true, false, true, releaseId));
        catalog.GetOffer(tenantId, agentId, "linux-x64", "0.4.101", "stable").Should().BeNull(
            "resume retains the failed immutable release suppression");
    }

    private static ClientUpdateReleaseRecord Release(string version, string channel, long revision) => new()
    {
        PublicId = Guid.NewGuid(),
        Revision = revision,
        RuntimeId = "linux-x64",
        Version = version,
        Channel = channel,
        ArtifactKey = $"linux-x64/{version}/client.zip",
        Sha256 = new string('a', 64),
        SizeBytes = 1024,
        ManifestJson = "{}",
        Enabled = true,
        PublishedAtUtc = DateTimeOffset.UtcNow
    };

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}

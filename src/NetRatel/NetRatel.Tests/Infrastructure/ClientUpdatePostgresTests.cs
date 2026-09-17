using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.Akka.Configuration;
using NetRatel.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ClientUpdatePostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private DbContextOptions<OrchestratorDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options;
        await using var db = new OrchestratorDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migration_Creates_Update_Authority_And_Enforces_Immutable_Versions()
    {
        await using var db = new OrchestratorDbContext(_options);
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema='public'")
            .ToListAsync();
        tables.Should().Contain(["ClientUpdateReleases", "ClientUpdateAttempts", "AgentClientUpdateStates", "ClientUpdateCatalogRevision"]);

        db.ClientUpdateReleases.Add(CreateRelease(Guid.NewGuid(), 1));
        await db.SaveChangesAsync();
        db.ClientUpdateReleases.Add(CreateRelease(Guid.NewGuid(), 2));
        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Concurrent_Claims_Are_Idempotent_And_Admission_Is_Connection_Fenced()
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var releasePublicId = Guid.NewGuid();
        await using (var seed = new OrchestratorDbContext(_options))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Canary", AutoUpdate = true });
            seed.Agents.Add(new Agent { Id = agentId, TenantId = tenantId });
            seed.ClientUpdateReleases.Add(CreateRelease(releasePublicId, 1));
            await seed.SaveChangesAsync();
        }

        var identity = new AuthenticatedAgentIdentity(tenantId, agentId);
        var nonce = new string('a', 64);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = new OrchestratorDbContext(_options);
            return await Authority(db).ClaimAsync(
                identity, releasePublicId, "linux-x64", "0.4.101", "stable", nonce, CancellationToken.None);
        }));

        claims.Should().OnlyContain(x => x != null);
        claims.Select(x => x!.AttemptId).Distinct().Should().ContainSingle();
        await using var authorityDb = new OrchestratorDbContext(_options);
        var authority = Authority(authorityDb);
        var attemptId = claims[0]!.AttemptId;
        await authority.ReportAsync(identity, attemptId, ClientUpdateAttemptState.Downloading, null, null, CancellationToken.None);
        await authority.ReportAsync(identity, attemptId, ClientUpdateAttemptState.Staged, null, null, CancellationToken.None);
        await authority.ReportAsync(identity, attemptId, ClientUpdateAttemptState.Activating, null, null, CancellationToken.None);

        var connectionId = Guid.NewGuid();
        var rejectedReadmission = await authority.MarkReadmittedAsync(identity, attemptId, releasePublicId, new string('b', 64),
            "0.4.102", connectionId, 4, CancellationToken.None);
        rejectedReadmission.Accepted.Should().BeFalse();
        rejectedReadmission.Reason.Should().Be("nonce_mismatch");
        (await authority.MarkReadmittedAsync(identity, attemptId, releasePublicId, nonce,
            "0.4.102", connectionId, 4, CancellationToken.None)).Accepted.Should().BeTrue();
        (await authority.ConfirmAsync(identity, attemptId, Guid.NewGuid(), 4, CancellationToken.None)).Reason
            .Should().Be("presence_connection_mismatch");
        (await authority.ConfirmAsync(identity, attemptId, connectionId, 4, CancellationToken.None)).ConfirmationId.Should().NotBeNull();

        var attempt = await authorityDb.ClientUpdateAttempts.AsNoTracking().SingleAsync();
        attempt.State.Should().Be(ClientUpdateAttemptState.Accepted);
        attempt.ConfirmationId.Should().NotBeNull();
    }

    [Fact]
    public async Task Interrupted_Download_Is_Immediately_Reissued_To_A_New_Admission()
    {
        const int tenantId = 74;
        var agentId = Guid.NewGuid();
        var releasePublicId = Guid.NewGuid();
        await using (var seed = new OrchestratorDbContext(_options))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Stale claim", AutoUpdate = true });
            seed.Agents.Add(new Agent { Id = agentId, TenantId = tenantId });
            seed.ClientUpdateReleases.Add(CreateRelease(releasePublicId, 2));
            await seed.SaveChangesAsync();
        }

        var identity = new AuthenticatedAgentIdentity(tenantId, agentId);
        var firstNonce = new string('a', 64);
        var replacementNonce = new string('b', 64);
        Guid attemptId;
        await using (var initialDb = new OrchestratorDbContext(_options))
        {
            var authority = Authority(initialDb);
            var initialClaim = await authority.ClaimAsync(
                identity, releasePublicId, "linux-x64", "0.4.101", "stable", firstNonce, CancellationToken.None);
            initialClaim.Should().NotBeNull();
            attemptId = initialClaim!.AttemptId;
            await authority.ReportAsync(identity, attemptId, ClientUpdateAttemptState.Downloading, null, null, CancellationToken.None);

            var freshReplacementClaim = await authority.ClaimAsync(
                identity, releasePublicId, "linux-x64", "0.4.101", "stable", replacementNonce, CancellationToken.None);
            freshReplacementClaim.Should().NotBeNull("a client restart must be able to resume an interrupted download");
            freshReplacementClaim!.AttemptId.Should().Be(attemptId);
        }

        await using var recoveryDb = new OrchestratorDbContext(_options);
        var recoveredAttempt = await recoveryDb.ClientUpdateAttempts.AsNoTracking().SingleAsync();
        recoveredAttempt.State.Should().Be(ClientUpdateAttemptState.Claimed);
        recoveredAttempt.AdmissionNonceHash.Should().NotBe(firstNonce);
    }

    [Fact]
    public async Task Stale_Claimed_Attempt_Is_Reissued_To_A_New_Admission()
    {
        const int tenantId = 75;
        var agentId = Guid.NewGuid();
        var releasePublicId = Guid.NewGuid();
        await using (var seed = new OrchestratorDbContext(_options))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Test", AutoUpdate = true, AutoUpdateChannel = "stable" });
            seed.Agents.Add(new Agent { Id = agentId, TenantId = tenantId });
            seed.ClientUpdateReleases.Add(CreateRelease(releasePublicId, 3));
            await seed.SaveChangesAsync();
        }

        var identity = new AuthenticatedAgentIdentity(tenantId, agentId);
        var firstNonce = new string('a', 64);
        var replacementNonce = new string('b', 64);
        await using (var initialDb = new OrchestratorDbContext(_options))
        {
            var initialClaim = await Authority(initialDb).ClaimAsync(
                identity, releasePublicId, "linux-x64", "0.4.101", "stable", firstNonce, CancellationToken.None);
            initialClaim.Should().NotBeNull();

            var freshReplacementClaim = await Authority(initialDb).ClaimAsync(
                identity, releasePublicId, "linux-x64", "0.4.101", "stable", replacementNonce, CancellationToken.None);
            freshReplacementClaim.Should().BeNull("a concurrent claim without an interrupted download remains fenced");
        }

        await using (var staleDb = new OrchestratorDbContext(_options))
        {
            var staleAttempt = await staleDb.ClientUpdateAttempts.SingleAsync();
            staleAttempt.UpdatedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
            await staleDb.SaveChangesAsync();
        }

        await using var recoveryDb = new OrchestratorDbContext(_options);
        var recoveryClaim = await Authority(recoveryDb).ClaimAsync(
            identity, releasePublicId, "linux-x64", "0.4.101", "stable", replacementNonce, CancellationToken.None);

        recoveryClaim.Should().NotBeNull();
        var recoveredAttempt = await recoveryDb.ClientUpdateAttempts.AsNoTracking().SingleAsync();
        recoveredAttempt.State.Should().Be(ClientUpdateAttemptState.Claimed);
        recoveredAttempt.AdmissionNonceHash.Should().NotBe(firstNonce);
    }

    [Fact]
    public async Task OperatorResume_ReenablesOnlyAnAuthoritySuspendedAutomaticUpdate()
    {
        const int tenantId = 75;
        var agentId = Guid.NewGuid();
        await using (var seed = new OrchestratorDbContext(_options))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Suspended update target", AutoUpdate = true });
            seed.Agents.Add(new Agent
            {
                Id = agentId,
                TenantId = tenantId,
                IsEnabled = true,
                Status = AgentStatus.Active,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            seed.AgentClientUpdateStates.Add(new AgentClientUpdateStateRecord
            {
                AgentId = agentId,
                TenantId = tenantId,
                SuspendedAtUtc = DateTimeOffset.UtcNow,
                SuspensionReason = "rollback",
                PolicyRevision = 4
            });
            await seed.SaveChangesAsync();
        }

        await using var db = new OrchestratorDbContext(_options);
        var authority = Authority(db);
        var eligibility = await authority.GetResumeEligibilityAsync(tenantId, agentId, CancellationToken.None);
        eligibility.Should().Be(new ClientUpdateResumeEligibility(true, null, 4));

        var result = await authority.ResumeAutomaticUpdatesAsync(tenantId, agentId, "operator-1", CancellationToken.None);

        result.Should().Be(new ClientUpdateResumeResult(true, null, 5));
        var state = await db.AgentClientUpdateStates.SingleAsync(candidate => candidate.AgentId == agentId);
        state.SuspendedAtUtc.Should().BeNull();
        state.SuspensionReason.Should().BeNull();
        state.PolicyRevision.Should().Be(5);
        state.ResumedBy.Should().Be("operator-1");
        (await authority.GetResumeEligibilityAsync(tenantId, agentId, CancellationToken.None)).FailureCode
            .Should().Be("client_update_not_suspended");
    }

    private static ClientUpdateAuthorityService Authority(OrchestratorDbContext db) => new(
        db,
        new NoOpCatalog(),
        new NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            GatewayEnabled = true,
            ClientUpdatesEnabled = true,
            PresenceAuthorityEnabled = true
        },
        TimeProvider.System,
        NullLogger<ClientUpdateAuthorityService>.Instance);

    private sealed class NoOpCatalog : IClientUpdateCatalog
    {
        public long Revision => 0;
        public DateTimeOffset RefreshedAtUtc => DateTimeOffset.UtcNow;
        public ClientUpdateOfferSnapshot? GetOffer(int tenantId, Guid agentId, string runtimeId, string currentVersion, string channel) => null;
        public ClientUpdatePolicySnapshot GetPolicy(int tenantId, Guid agentId, long clientPolicyRevision) => new(0, false, false, false, null);
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static ClientUpdateReleaseRecord CreateRelease(Guid publicId, long revision) => new()
    {
        PublicId = publicId,
        Revision = revision,
        RuntimeId = "linux-x64",
        Version = "0.4.102",
        Channel = "stable",
        ArtifactKey = "linux-x64/0.4.102/NetRatel.Client-linux-x64-0.4.102.zip",
        Sha256 = new string('a', 64),
        SizeBytes = 123,
        ManifestJson = "{}",
        Enabled = true,
        PublishedAtUtc = DateTimeOffset.UtcNow
    };
}

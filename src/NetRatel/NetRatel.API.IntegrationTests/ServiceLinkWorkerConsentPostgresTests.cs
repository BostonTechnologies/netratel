using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

[Collection(ServiceLinkRealPeerCollection.Name)]
public sealed class ServiceLinkWorkerConsentPostgresTests
{
    [Theory]
    [InlineData("responder-consent")]
    [InlineData("initiator-callback")]
    [InlineData("initiator-consent")]
    public async Task Postgres_worker_does_not_write_a_live_human_consent_attempt_between_its_read_and_save(string step)
    {
        var interleaving = new HumanConsentWorkerInterleaving(step);
        await using var pair = await ServiceLinkPair.CreateAsync(step != "responder-consent", interleaving);
        await pair.PrepareAsync();
        if (step == "initiator-consent") await pair.ReviewAsync();
        interleaving.Arm(pair.NetRatel.Services, pair.Start.AttemptId);

        if (step == "responder-consent")
        {
            var state = QueryHelpers.ParseQuery(new Uri(pair.Start.NavigationUrl).Query)["browser_state"].ToString();
            using var response = await pair.NetRatel.Administrator.PostAsJsonAsync("/api/v1/admin/service-links/remote-approve",
                new ServiceLinkRemoteApproveRequest(pair.Start.AttemptId, pair.NetRatel.TenantId,
                    pair.Descriptor.RequestedGrants, state) { SessionBinding = pair.SessionBinding });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else if (step == "initiator-callback") await pair.ReviewAsync();
        else await pair.ApproveAsync();

        Assert.Equal(1, interleaving.Executions);
        Assert.NotEqual(interleaving.HumanContextId, interleaving.WorkerContextId);
        interleaving.AssertAttemptUnchanged();
        await using var final = pair.NetRatel.Services.CreateAsyncScope();
        var db = final.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var approved = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync(a => a.AttemptId == pair.Start.AttemptId);
        Assert.Equal("approved", approved.LifecycleState);
        Assert.Equal("undecided", approved.Decision);
        Assert.False(approved.LocalInboundActive);
        Assert.False(approved.LocalBusinessSenderEnabled);
        Assert.NotNull(approved.GrantHash);
        var expectedPrincipals = step == "initiator-callback" ? 0 : 1;
        Assert.Equal(expectedPrincipals, await db.Set<ServicePrincipalRegistration>().CountAsync());
        Assert.Equal(expectedPrincipals, await db.Set<ServicePrincipalSecret>().CountAsync());
        Assert.Equal(0, await db.Set<ServiceLinkOperation>().CountAsync());
        Assert.Equal(0, await db.Set<ServiceLinkVerificationReceipt>().CountAsync());
        Assert.Equal(0, await db.Set<ServiceLinkRotation>().CountAsync());
    }

    [Theory]
    [InlineData("initiator-awaiting")]
    [InlineData("responder-awaiting")]
    [InlineData("initiator-approved")]
    public async Task Postgres_worker_expires_human_waits_without_extending_deadlines_or_creating_authority(string state)
    {
        await using var pair = await ServiceLinkPair.CreateAsync(state != "responder-awaiting");
        await pair.PrepareAsync();
        if (state == "initiator-approved") await pair.ReviewAsync();
        var before = await ReadAttemptAsync(pair);
        Assert.Null(before.InboundPrincipalId);
        Assert.Equal("undecided", before.Decision);
        // This deterministic expiry check uses the existing fixture clock. It is
        // PostgreSQL lifecycle coverage, not real-time rotation or physical proof.
        pair.NetRatel.Clock.Advance(TimeSpan.FromSeconds(before.ExpiresAtUnixSeconds -
            pair.NetRatel.Clock.GetUtcNow().ToUnixTimeSeconds() + 1));
        await using (var work = pair.NetRatel.Services.CreateAsyncScope())
            await work.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(CancellationToken.None);
        var expired = await ReadAttemptAsync(pair);
        Assert.Equal("expired", expired.LifecycleState);
        Assert.Equal("abort", expired.Decision);
        Assert.NotNull(expired.AbortId);
        Assert.Equal(before.ExpiresAtUnixSeconds, expired.ExpiresAtUnixSeconds);
        Assert.Equal(before.DescriptorHash, expired.DescriptorHash);
        Assert.Equal(before.GrantHash, expired.GrantHash);
        Assert.Null(expired.ActiveRelationshipKey);
        Assert.Null(expired.InboundPrincipalId);
        Assert.False(expired.LocalInboundActive);
        Assert.False(expired.LocalBusinessSenderEnabled);
        Assert.True(expired.ProtectedBrowserState is null && expired.ProtectedVerifier is null &&
            expired.ProtectedPairingCode is null && expired.ProtectedInboundEscrow is null &&
            expired.ProtectedExchangeResponse is null, "Expired unused consent retained bootstrap escrow.");
        await using (var check = pair.NetRatel.Services.CreateAsyncScope())
            await AssertNoAuthorityAsync(check.ServiceProvider.GetRequiredService<OrchestratorDbContext>(), CancellationToken.None);
        await using (var repeatedWork = pair.NetRatel.Services.CreateAsyncScope())
            await repeatedWork.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(CancellationToken.None);
        Assert.Equal(expired.Revision, (await ReadAttemptAsync(pair)).Revision);
    }

    private static async Task<ServiceLinkAttempt> ReadAttemptAsync(ServiceLinkPair pair)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<ServiceLinkAttempt>()
            .AsNoTracking().SingleAsync(a => a.AttemptId == pair.Start.AttemptId);
    }

    private static async Task AssertNoAuthorityAsync(OrchestratorDbContext db, CancellationToken ct)
    {
        Assert.Equal(0, await db.Set<ServicePrincipalRegistration>().CountAsync(ct));
        Assert.Equal(0, await db.Set<ServicePrincipalSecret>().CountAsync(ct));
        Assert.Equal(0, await db.Set<ServiceLinkOperation>().CountAsync(ct));
        Assert.Equal(0, await db.Set<ServiceLinkVerificationReceipt>().CountAsync(ct));
        Assert.Equal(0, await db.Set<ServiceLinkRotation>().CountAsync(ct));
    }

    private sealed class HumanConsentWorkerInterleaving(string step) : SaveChangesInterceptor
    {
        private IServiceProvider services = null!;
        private string attemptId = "";
        private int armed;
        public int Executions { get; private set; }
        public Guid HumanContextId { get; private set; }
        public Guid WorkerContextId { get; private set; }
        private long beforeRevision, afterRevision, beforeNextWork, afterNextWork;
        private bool attemptUnchanged;
        public void Arm(IServiceProvider provider, string id) { services = provider; attemptId = id; Volatile.Write(ref armed, 1); }
        public void AssertAttemptUnchanged()
        {
            Assert.Equal(beforeRevision, afterRevision);
            Assert.Equal(beforeNextWork, afterNextWork);
            Assert.True(attemptUnchanged, "Background work changed the durable live human-consent attempt.");
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref armed) == 0 || eventData.Context is not { } human) return result;
            var attempt = human.ChangeTracker.Entries<ServiceLinkAttempt>().SingleOrDefault(e => e.Entity.AttemptId == attemptId);
            if (attempt is null || (step == "initiator-callback"
                    ? attempt.State != EntityState.Modified || attempt.Entity.LifecycleState != "approved"
                    : !human.ChangeTracker.Entries<ServicePrincipalRegistration>().Any(e => e.State == EntityState.Added && e.Entity.AttemptId == attemptId)))
                return result;
            if (Interlocked.Exchange(ref armed, 0) == 0) return result;
            HumanContextId = human.ContextId.InstanceId;
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            WorkerContextId = db.ContextId.InstanceId;
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
            var before = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync(a => a.AttemptId == attemptId, cancellationToken);
            Assert.Equal(step == "initiator-consent" ? "approved" : "awaiting_approval", before.LifecycleState);
            Assert.Equal("undecided", before.Decision);
            Assert.Null(before.InboundPrincipalId);
            Assert.True(before.ExpiresAtUnixSeconds > services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds());
            await AssertNoAuthorityAsync(db, cancellationToken);
            // Suspend the actual human save, run the production worker in another
            // real PostgreSQL scope, then let that same HTTP/EF scope complete.
            // No provider error, row revision, consent, principal or response is fabricated.
            await scope.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(cancellationToken);
            var after = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync(a => a.AttemptId == attemptId, cancellationToken);
            beforeRevision = before.Revision; afterRevision = after.Revision;
            beforeNextWork = before.NextWorkAtUnixSeconds; afterNextWork = after.NextWorkAtUnixSeconds;
            attemptUnchanged = string.Equals(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after), StringComparison.Ordinal);
            await AssertNoAuthorityAsync(db, cancellationToken);
            Executions++;
            // Compare the recorded snapshots only after this command returns,
            // so the old worker behavior reaches its real EF concurrency failure
            // rather than being replaced by an assertion in the interceptor.
            return result;
        }
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed partial class ServiceLinkProfileTokenExpiryPostgresTests
{
    [Fact]
    public async Task Disabled_worker_sweeps_expired_bootstrap_secrets_in_bounded_batches_without_changing_recovery_decisions()
    {
        await using var fixture = await TokenProfileFixture.CreateAsync(postgres, 10);
        var originals = await fixture.SeedTemporaryEscrowsAsync(25);
        var coordinator = fixture.CleanupCoordinator();

        await coordinator.WorkAsync(CancellationToken.None);
        var afterFirst = await fixture.ReadAttemptsAsync(originals);
        Assert.Equal(20, afterFirst.Count(x => x.ExpiresAtUnixSeconds <= fixture.Clock.GetUtcNow().ToUnixTimeSeconds() && !HasTemporaryEscrow(x)));
        Assert.Equal(5, afterFirst.Count(x => x.ExpiresAtUnixSeconds <= fixture.Clock.GetUtcNow().ToUnixTimeSeconds() && HasTemporaryEscrow(x)));
        Assert.Equal(0, fixture.PeerHttpRequests);
        AssertUnchangedRecovery(originals, afterFirst);

        await coordinator.WorkAsync(CancellationToken.None);
        var afterSecond = await fixture.ReadAttemptsAsync(originals);
        Assert.All(afterSecond.Where(x => x.ExpiresAtUnixSeconds <= fixture.Clock.GetUtcNow().ToUnixTimeSeconds()), row =>
        {
            Assert.False(HasTemporaryEscrow(row));
            Assert.Equal(originals.Single(x => x.AttemptId == row.AttemptId).Revision + 1, row.Revision);
        });
        AssertUnchangedRecovery(originals, afterSecond);
        Assert.Contains(afterSecond, row => row.LifecycleState == "prepared" && row.Decision == "commit" && !HasTemporaryEscrow(row));
        Assert.Contains(afterSecond, row => row.LifecycleState == "in_doubt" && row.Decision == "undecided" && !HasTemporaryEscrow(row));
        Assert.Contains(afterSecond, row => row.LifecycleState == "awaiting_approval" && !HasTemporaryEscrow(row));
        foreach (var recoverable in afterSecond.Where(x => x.ProtectedOutboundCredential is not null))
            fixture.AssertRecoveryCredentialReadable(recoverable);
        foreach (var future in afterSecond.Where(x => x.ExpiresAtUnixSeconds > fixture.Clock.GetUtcNow().ToUnixTimeSeconds()))
        {
            var before = originals.Single(x => x.AttemptId == future.AttemptId);
            Assert.Equal(before.Revision, future.Revision);
            Assert.Equal(before.ProtectedVerifier, future.ProtectedVerifier);
            Assert.Equal(before.ProtectedBrowserState, future.ProtectedBrowserState);
            Assert.Equal(before.ProtectedPairingCode, future.ProtectedPairingCode);
            Assert.Equal(before.ProtectedInboundEscrow, future.ProtectedInboundEscrow);
            Assert.Equal(before.ProtectedExchangeResponse, future.ProtectedExchangeResponse);
        }
        Assert.Equal(0, fixture.PeerHttpRequests);

        // Sweeping again has no eligible bootstrap material and cannot revise
        // either an already swept decision or a future unexpired ceremony.
        await coordinator.WorkAsync(CancellationToken.None);
        var afterThird = await fixture.ReadAttemptsAsync(originals);
        Assert.All(afterThird, row => Assert.Equal(afterSecond.Single(x => x.AttemptId == row.AttemptId).Revision, row.Revision));
        Assert.Equal(0, fixture.PeerHttpRequests);
    }

    [Fact]
    public async Task Bootstrap_escrow_is_swept_before_invalid_current_public_settings_are_resolved()
    {
        await using var fixture = await TokenProfileFixture.CreateAsync(postgres, 10);
        var originals = await fixture.SeedTemporaryEscrowsAsync(1);
        var coordinator = fixture.CleanupCoordinator(invalidSettings: true);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => coordinator.WorkAsync(CancellationToken.None));
        Assert.Equal("Synthetic invalid enabled public identity.", error.Message);

        var after = await fixture.ReadAttemptsAsync(originals);
        var expired = Assert.Single(after, x => x.ExpiresAtUnixSeconds <= fixture.Clock.GetUtcNow().ToUnixTimeSeconds());
        Assert.False(HasTemporaryEscrow(expired));
        Assert.Equal(originals.Single(x => x.AttemptId == expired.AttemptId).Revision + 1, expired.Revision);
        AssertUnchangedRecovery(originals, after);
        Assert.All(after.Where(x => x.ExpiresAtUnixSeconds > fixture.Clock.GetUtcNow().ToUnixTimeSeconds()), row => Assert.True(HasTemporaryEscrow(row)));
        Assert.Equal(0, fixture.PeerHttpRequests);
    }

    private static bool HasTemporaryEscrow(ServiceLinkAttempt row) => row.ProtectedInboundEscrow is not null ||
        row.ProtectedExchangeResponse is not null || row.ProtectedPairingCode is not null ||
        row.ProtectedVerifier is not null || row.ProtectedBrowserState is not null;

    private static void AssertUnchangedRecovery(ServiceLinkAttempt[] originals, ServiceLinkAttempt[] actual)
    {
        Assert.Equal(originals.Length, actual.Length);
        foreach (var row in actual)
        {
            var before = originals.Single(x => x.AttemptId == row.AttemptId);
            Assert.Equal(before.LifecycleState, row.LifecycleState);
            Assert.Equal(before.Decision, row.Decision);
            Assert.Equal(before.CommitId, row.CommitId);
            Assert.Equal(before.ConsentId, row.ConsentId);
            Assert.Equal(before.LinkId, row.LinkId);
            Assert.Equal(before.LinkRevision, row.LinkRevision);
            Assert.Equal(before.GrantHash, row.GrantHash);
            Assert.Equal(before.DescriptorHash, row.DescriptorHash);
            Assert.Equal(before.DescriptorJson, row.DescriptorJson);
            Assert.Equal(before.GrantSummaryJson, row.GrantSummaryJson);
            Assert.Equal(before.ProtectedOutboundCredential, row.ProtectedOutboundCredential);
            Assert.Equal(before.OutboundProfileRevision, row.OutboundProfileRevision);
            Assert.Equal(before.ExpiresAtUnixSeconds, row.ExpiresAtUnixSeconds);
            Assert.Equal(before.TerminalControlExpiresAtUnixSeconds, row.TerminalControlExpiresAtUnixSeconds);
            Assert.Equal(before.LocalInboundActive, row.LocalInboundActive);
            Assert.Equal(before.LocalBusinessSenderEnabled, row.LocalBusinessSenderEnabled);
            Assert.Equal(before.PeerActiveAcknowledged, row.PeerActiveAcknowledged);
            Assert.Equal(before.NextWorkAtUnixSeconds, row.NextWorkAtUnixSeconds);
            Assert.Equal(before.LastErrorCode, row.LastErrorCode);
        }
    }

    private sealed partial class TokenProfileFixture
    {
        public ServiceLinkCoordinator CleanupCoordinator(bool invalidSettings = false)
        {
            var settings = new ServicePublicSettingsEffective(new ServiceIdentityOptions { Enabled = false },
                new ServiceLinkOptions { Enabled = false }, 1, []);
            IServicePublicSettingsResolver resolver = invalidSettings ? new InvalidPublicSettings() : new FixturePublicSettings(settings);
            var db = profileScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var registry = new ServicePrincipalRegistry(db, new ServiceIdentityRuntimeOptions(resolver),
                app.Services.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>(), resolver, new EmptyServiceClientDeploymentCatalog(), Clock);
            var transport = new ServiceLinkTransport(client, Options.Create(settings.Linking));
            return new(db, registry, new UnexpectedHumanAccess(), profiles,
                transport, app.Services.GetRequiredService<IDataProtectionProvider>(), Options.Create(settings.Linking), resolver, Clock,
                new ServiceLinkProtocolTokenCache(transport,
                    app.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), Clock));
        }

        public async Task<ServiceLinkAttempt[]> SeedTemporaryEscrowsAsync(int expiredCount)
        {
            var db = profileScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var now = Clock.GetUtcNow().ToUnixTimeSeconds();
            var originals = new List<ServiceLinkAttempt>();
            for (var index = 0; index < expiredCount + 3; index++)
            {
                var prepared = index % 3 == 0; var inDoubt = index % 3 == 1;
                var row = new ServiceLinkAttempt
                {
                    AttemptId = ServiceLinkValidation.NewId(), Role = inDoubt ? "responder" : "initiator",
                    LocalTenantId = LocalTenant, LocalActorId = "synthetic-administrator",
                    PeerInstanceId = Profile.PeerInstanceId, PeerTenantId = PeerTenant,
                    LinkId = ServiceLinkValidation.NewId(), LinkRevision = 7,
                    LifecycleState = prepared ? "prepared" : inDoubt ? "in_doubt" : "awaiting_approval",
                    Decision = prepared ? "commit" : "undecided", CommitId = prepared ? ServiceLinkValidation.NewId() : null,
                    ConsentId = ServiceLinkValidation.NewId(), DescriptorHash = ServiceLinkValidation.Digest("synthetic-escrow-descriptor"),
                    DescriptorJson = "{}", GrantSummaryJson = "{}", GrantHash = ServiceLinkValidation.Digest("synthetic-escrow-grant"),
                    OutboundProfileRevision = prepared || inDoubt ? 4 : null,
                    ExpiresAtUnixSeconds = index < expiredCount ? now - expiredCount + index : now + 600,
                    TerminalControlExpiresAtUnixSeconds = now + 600, CreatedAtUnixSeconds = now - 3600,
                    UpdatedAtUnixSeconds = now - 1800, NextWorkAtUnixSeconds = now - 1, Revision = 5
                };
                string Protected(string purpose) => app.Services.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("NetRatel.ServiceLink.v1", row.AttemptId, row.PeerInstanceId, purpose,
                        purpose is "verifier" or "browser-state" or "pairing-code" ? "bootstrap" :
                            row.LocalTenantId + "/" + row.LinkId + "/" + row.GrantHash + "/" + row.LinkRevision)
                    .Protect(purpose == "outbound-credential" ? Serialize(outbound) : "synthetic-escrow-material");
                row.ProtectedInboundEscrow = Protected("inbound-escrow");
                row.ProtectedExchangeResponse = Protected("exchange-response");
                row.ProtectedPairingCode = Protected("pairing-code");
                row.ProtectedVerifier = Protected("verifier");
                row.ProtectedBrowserState = Protected("browser-state");
                if (prepared || inDoubt) row.ProtectedOutboundCredential = Protected("outbound-credential");
                originals.Add(row);
                db.Set<ServiceLinkAttempt>().Add(row);
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return originals.ToArray();
        }

        public void AssertRecoveryCredentialReadable(ServiceLinkAttempt row)
        {
            var clear = app.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("NetRatel.ServiceLink.v1", row.AttemptId, row.PeerInstanceId, "outbound-credential",
                    row.LocalTenantId + "/" + row.LinkId + "/" + row.GrantHash + "/" + row.LinkRevision)
                .Unprotect(row.ProtectedOutboundCredential!);
            var retained = ServiceLinkCanonicalJson.Deserialize<ServiceDirectionalCredential>(clear);
            Assert.Equal(ServiceLinkCanonicalJson.HashObject(outbound), ServiceLinkCanonicalJson.HashObject(retained));
        }

        public async Task<ServiceLinkAttempt[]> ReadAttemptsAsync(ServiceLinkAttempt[] originals)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var ids = originals.Select(x => x.AttemptId).ToArray();
            return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<ServiceLinkAttempt>()
                .AsNoTracking().Where(x => ids.Contains(x.AttemptId)).ToArrayAsync();
        }
    }

    private sealed class InvalidPublicSettings : IServicePublicSettingsResolver
    {
        public Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default) =>
            throw new ArgumentException("Synthetic invalid enabled public identity.");
        public Task<ServicePublicSettingsEffective> UpdateAsync(ServicePublicSettingsUpdate update, string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnexpectedHumanAccess : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Disabled escrow disposal must not authorize human or business actions.");
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Disabled escrow disposal must not resolve human grants.");
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

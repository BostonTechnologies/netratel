using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed partial class ServiceLinkProfileTokenExpiryPostgresTests
{
    [Theory]
    [InlineData("sender-disabled")]
    [InlineData("unlinked")]
    [InlineData("inbound-revoked")]
    public async Task Completed_authority_change_while_valid_token_body_is_held_denies_token_and_leaves_cache_empty(string change)
    {
        await using var fixture = await TokenProfileFixture.CreateAsync(postgres, 30);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = fixture.HoldNextTokenResponse();
        Assert.Empty(fixture.TokenCacheKeys());
        var token = fixture.GetTokenAsync(deadline.Token);
        try
        {
            await response.WaitForSuccessfulHeadersAsync(deadline.Token);
            Assert.False(token.IsCompleted);
            Assert.Equal(1, fixture.TokenRequests);
            Assert.Equal(0, fixture.InvalidTokenRequests);
            Assert.Empty(fixture.TokenCacheKeys());

            // The mutation uses another production DbContext. A third scope reads
            // committed authority before the successful token body is released.
            await fixture.ChangeAuthorityAsync(change, deadline.Token);
            await fixture.AssertCommittedAuthorityChangeAsync(change, deadline.Token);
            Assert.False(token.IsCompleted);
            response.Release();

            var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => token.WaitAsync(deadline.Token));
            Assert.Equal(403, error.StatusCode);
            Assert.Equal("grant-unavailable", error.Code);
            await response.WaitForCompletedResponseAsync(deadline.Token);
            Assert.Empty(fixture.TokenCacheKeys());

            // Denied work cannot implicitly retry HTTP or recover a denied bearer
            // from the token cache on a subsequent call with the captured profile.
            var again = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => fixture.GetTokenAsync(deadline.Token));
            Assert.Equal(403, again.StatusCode);
            Assert.Equal("grant-unavailable", again.Code);
            Assert.Equal(1, fixture.TokenRequests);
            Assert.Equal(1, fixture.PeerHttpRequests);
            Assert.Equal(0, fixture.InvalidTokenRequests);
            Assert.Empty(fixture.TokenCacheKeys());
        }
        finally
        {
            response.Release();
            // Observe a pending request before the owned DbContext/HTTP host is
            // disposed, even if a pre-release assertion or durable save failed.
            await ObserveHeldRequestCleanupAsync(token, deadline, allowProtocolDenial: true);
        }
    }

    [Fact]
    public async Task Unchanged_enabled_authority_allows_held_valid_token_and_reuses_the_same_cached_bearer()
    {
        await using var fixture = await TokenProfileFixture.CreateAsync(postgres, 30);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = fixture.HoldNextTokenResponse();
        Assert.Empty(fixture.TokenCacheKeys());
        var token = fixture.GetTokenAsync(deadline.Token);
        try
        {
            await response.WaitForSuccessfulHeadersAsync(deadline.Token);
            Assert.False(token.IsCompleted);
            await fixture.AssertCommittedEnabledAuthorityAsync(deadline.Token);
            Assert.Empty(fixture.TokenCacheKeys());
            response.Release();

            var issued = await token.WaitAsync(deadline.Token);
            Assert.Equal("synthetic-token-1", issued);
            await response.WaitForCompletedResponseAsync(deadline.Token);
            Assert.Single(fixture.TokenCacheKeys());
            Assert.Equal(issued, await fixture.GetTokenAsync(deadline.Token));
            Assert.Single(fixture.TokenCacheKeys());
            Assert.Equal(1, fixture.TokenRequests);
            Assert.Equal(1, fixture.PeerHttpRequests);
            Assert.Equal(0, fixture.InvalidTokenRequests);
        }
        finally
        {
            response.Release();
            await ObserveHeldRequestCleanupAsync(token, deadline, allowProtocolDenial: false);
        }
    }

    private static async Task ObserveHeldRequestCleanupAsync(Task<string> token, CancellationTokenSource deadline, bool allowProtocolDenial)
    {
        if (!token.IsCompleted) deadline.Cancel();
        try { await token.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (ServiceLinkProtocolException) when (allowProtocolDenial) { }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
    }

    private sealed partial class TokenProfileFixture
    {
        private readonly ServicePublicSettingsEffective publicSettings;
        private HeldTokenResponse? nextHeldResponse;

        public HeldTokenResponse HoldNextTokenResponse()
        {
            var held = new HeldTokenResponse();
            if (Interlocked.CompareExchange(ref nextHeldResponse, held, null) is not null)
                throw new InvalidOperationException("A token response is already held.");
            return held;
        }

        public string[] TokenCacheKeys() => ((MemoryCache)app.Services.GetRequiredService<IMemoryCache>()).Keys
            .OfType<string>().Where(key => key.StartsWith("netratel-service/", StringComparison.Ordinal)).ToArray();

        public async Task ChangeAuthorityAsync(string change, CancellationToken ct)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var resolver = new FixturePublicSettings(publicSettings);
            var registry = new ServicePrincipalRegistry(db, new ServiceIdentityRuntimeOptions(resolver),
                app.Services.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>(), resolver,
                new EmptyServiceClientDeploymentCatalog(), Clock);
            var transport = new ServiceLinkTransport(client, Options.Create(publicSettings.Linking));
            var scopedProfiles = new ServiceLinkProfileService(db, resolver,
                app.Services.GetRequiredService<IDataProtectionProvider>(), transport,
                app.Services.GetRequiredService<IMemoryCache>(), Clock);
            var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync(x => x.LinkId == Profile.LinkId, ct);
            switch (change)
            {
                case "sender-disabled":
                    await scopedProfiles.SetSenderAsync(attempt, attempt.OutboundProfileRevision!.Value, false, ct);
                    attempt.Revision++;
                    await db.SaveChangesAsync(ct);
                    break;
                case "inbound-revoked":
                    await registry.RevokeAsync(inboundPrincipalId, ct);
                    break;
                case "unlinked":
                    var principal = await db.Set<ServicePrincipalRegistration>().AsNoTracking()
                        .SingleAsync(x => x.Id == inboundPrincipalId, ct);
                    var caller = ControlCaller(principal);
                    // This is the production authenticated revoke operation, not
                    // a fixture-written terminal state. The accepted incoming
                    // unlink commits local revocation and a completed operation.
                    var coordinator = new ServiceLinkCoordinator(db, registry, new UnexpectedHumanAccess(), scopedProfiles,
                        transport, app.Services.GetRequiredService<IDataProtectionProvider>(),
                        Options.Create(publicSettings.Linking), resolver, Clock,
                        new ServiceLinkProtocolTokenCache(transport, app.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), Clock));
                    await coordinator.LifecycleAsync(Profile.LinkId, "revoke", new ServiceLinkLifecycleRequest
                    {
                        OperationId = ServiceLinkValidation.NewId(), AttemptId = attempt.AttemptId,
                        LinkId = attempt.LinkId!, LinkRevision = attempt.LinkRevision, GrantHash = attempt.GrantHash!,
                        ExpectedLinkRevision = attempt.LinkRevision, RevocationId = ServiceLinkValidation.NewId(),
                        ReasonCode = "synthetic-regression-unlink"
                    }, caller, ct);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
        }

        public async Task AssertCommittedAuthorityChangeAsync(string change, CancellationToken ct)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var attempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync(x => x.LinkId == Profile.LinkId, ct);
            var principal = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleAsync(x => x.Id == inboundPrincipalId, ct);
            Assert.False(attempt.LocalBusinessSenderEnabled);
            Assert.Equal(Profile.LinkRevision, attempt.LinkRevision);
            Assert.Equal(Profile.GrantHash, attempt.GrantHash);
            if (change == "sender-disabled")
            {
                Assert.True(attempt.LocalInboundActive);
                Assert.Equal("active", attempt.LifecycleState);
                Assert.Equal("active", principal.Status);
                Assert.Equal(Profile.ProfileRevision + 1, attempt.OutboundProfileRevision);
            }
            else
            {
                Assert.False(attempt.LocalInboundActive);
                Assert.Equal("revoked", principal.Status);
                Assert.NotNull(principal.RevokedAtUtc);
                if (change == "unlinked")
                {
                    Assert.Equal("revoked", attempt.LifecycleState);
                    Assert.NotNull(attempt.RevocationId);
                    Assert.Equal(Profile.ProfileRevision + 1, attempt.OutboundProfileRevision);
                    Assert.Equal(1, await db.Set<ServiceLinkOperation>().AsNoTracking()
                        .CountAsync(x => x.LinkId == Profile.LinkId && x.Kind == "revoke" && !x.Outbound && x.Completed, ct));
                }
                else Assert.Equal("active", attempt.LifecycleState);
            }
        }

        public async Task AssertCommittedEnabledAuthorityAsync(CancellationToken ct)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var attempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync(x => x.LinkId == Profile.LinkId, ct);
            Assert.True(attempt.LocalBusinessSenderEnabled);
            Assert.True(attempt.LocalInboundActive);
            Assert.Equal("active", attempt.LifecycleState);
            Assert.Equal(Profile.ProfileRevision, attempt.OutboundProfileRevision);
            Assert.True(await ServiceLinkAuthority.InboundUsableAsync(db, attempt, Clock, publicSettings, ct));
        }

        private static ClaimsPrincipal ControlCaller(ServicePrincipalRegistration principal) => new(new ClaimsIdentity(new[]
        {
            new Claim("auth_mode", "service"), new Claim("token_use", ServiceIdentityClaims.Purpose),
            new Claim("sub", $"service:{principal.Id:N}"), new Claim("client_id", principal.ClientId),
            new Claim("scope", ServiceLinkContract.ControlScope),
            new Claim(ServiceIdentityClaims.PrincipalId, principal.Id.ToString("N")),
            new Claim(ServiceIdentityClaims.CredentialRevision, "1"),
            new Claim(ServiceIdentityClaims.GrantRevision, principal.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ServiceIdentityClaims.TenantId, LocalTenant),
            new Claim(ServiceIdentityClaims.PeerInstanceId, principal.PeerInstanceId!),
            new Claim(ServiceIdentityClaims.PeerTenantId, principal.PeerTenantId!),
            new Claim(ServiceIdentityClaims.LinkId, principal.LinkId!), new Claim(ServiceIdentityClaims.AttemptId, principal.AttemptId!),
            new Claim(ServiceIdentityClaims.GrantHash, principal.GrantHash!), new Claim(ServiceIdentityClaims.DirectionId, principal.DirectionId!),
            new Claim(ServiceIdentityClaims.LinkRevision, principal.LinkRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))
        }, "synthetic-service-control"));

        public sealed class HeldTokenResponse : IResult, IDisposable
        {
            private readonly TaskCompletionSource headers = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private string? body;

            public IResult Prepare(string value) { body = value; return this; }
            public Task WaitForSuccessfulHeadersAsync(CancellationToken ct) => headers.Task.WaitAsync(ct);
            public Task WaitForCompletedResponseAsync(CancellationToken ct) => completed.Task.WaitAsync(ct);
            public void Release() => release.TrySetResult();
            public void Dispose() => Release();

            public async Task ExecuteAsync(HttpContext context)
            {
                var json = body ?? throw new InvalidOperationException("The held token response has no validated body.");
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength = Encoding.UTF8.GetByteCount(json);
                await context.Response.StartAsync(context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                headers.TrySetResult();
                await release.Task.WaitAsync(context.RequestAborted);
                await context.Response.WriteAsync(json, context.RequestAborted);
                completed.TrySetResult();
            }
        }
    }
}

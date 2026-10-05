using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
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

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed partial class ServiceLinkProfileTokenExpiryPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Short_lived_peer_token_is_reused_before_skew_and_reacquired_at_six_and_eleven_seconds()
    {
        await using var fixture = await TokenProfileFixture.CreateAsync(postgres, 10);
        var first = await fixture.GetTokenAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(first, await fixture.GetTokenAsync());
        Assert.Equal(1, fixture.TokenRequests);

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        var second = await fixture.GetTokenAsync();
        Assert.NotEqual(first, second);
        Assert.Equal(2, fixture.TokenRequests);

        // The second response at t+6 has a reuse deadline of t+11. Equality
        // with that deadline must acquire a third token, not reuse the second.
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var third = await fixture.GetTokenAsync();
        Assert.NotEqual(second, third);
        Assert.Equal(3, fixture.TokenRequests);
        Assert.Equal(0, fixture.InvalidTokenRequests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Peer_tokens_inside_five_second_skew_are_never_cached(int expiresIn)
    {
        await using var fixture = await TokenProfileFixture.CreateAsync(postgres, expiresIn);
        var first = await fixture.GetTokenAsync();
        var second = await fixture.GetTokenAsync();
        Assert.NotEqual(first, second);
        Assert.Equal(2, fixture.TokenRequests);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(second, await fixture.GetTokenAsync());
        Assert.Equal(3, fixture.TokenRequests);
    }

    [Fact]
    public async Task Http_response_transit_consuming_reuse_window_prevents_caching()
    {
        await using var fixture = await TokenProfileFixture.CreateAsync(postgres, 10);
        var startedAt = fixture.Clock.GetUtcNow();
        fixture.AdvanceDuringNextResponse(TimeSpan.FromSeconds(6));
        var first = await fixture.GetTokenAsync();
        Assert.Equal(startedAt.AddSeconds(6), fixture.Clock.GetUtcNow());

        // Six seconds elapsed inside the real HTTP endpoint, so the first
        // response already consumed its five-second conservative reuse window.
        var second = await fixture.GetTokenAsync();
        Assert.NotEqual(first, second);
        Assert.Equal(2, fixture.TokenRequests);
        Assert.Equal(second, await fixture.GetTokenAsync());
        Assert.Equal(2, fixture.TokenRequests);
    }

    [Fact]
    public async Task Long_lived_peer_token_is_reacquired_at_forty_five_second_reuse_limit()
    {
        await using var fixture = await TokenProfileFixture.CreateAsync(postgres, 900);
        var first = await fixture.GetTokenAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(44));
        Assert.Equal(first, await fixture.GetTokenAsync());
        Assert.Equal(1, fixture.TokenRequests);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(first, await fixture.GetTokenAsync());
        Assert.Equal(2, fixture.TokenRequests);
    }

    [Fact]
    public async Task Invalid_peer_lifetimes_are_rejected_and_do_not_poison_profile_cache()
    {
        await using var fixture = await TokenProfileFixture.CreateAsync(postgres, 10);
        // Null here omits the field; "null" supplies a JSON null value.
        string?[] invalidLifetimes = [null, "\"10\"", "0", "-1", "901", "null", "true", "{}"];
        foreach (var lifetime in invalidLifetimes)
        {
            fixture.ExpiresInJson = lifetime;
            var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => fixture.GetTokenAsync());
            Assert.Equal(422, error.StatusCode);
            Assert.Equal("invalid-token-response", error.Code);
        }
        Assert.Equal(invalidLifetimes.Length, fixture.TokenRequests);

        fixture.ExpiresInJson = "10";
        var valid = await fixture.GetTokenAsync();
        Assert.Equal(valid, await fixture.GetTokenAsync());
        Assert.Equal(invalidLifetimes.Length + 1, fixture.TokenRequests);
        Assert.Equal(0, fixture.InvalidTokenRequests);
    }

    [Fact]
    public async Task Cached_peer_token_still_requires_current_durable_inbound_authority()
    {
        await using var fixture = await TokenProfileFixture.CreateAsync(postgres, 10);
        _ = await fixture.GetTokenAsync();
        await fixture.RevokeInboundAsync();

        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => fixture.GetTokenAsync());
        Assert.Equal(403, error.StatusCode);
        Assert.Equal("grant-unavailable", error.Code);
        Assert.Equal(1, fixture.TokenRequests);
    }

    /// <summary>
    /// A synthetic approved relationship exercises production profile resolution,
    /// live PostgreSQL authority, protected credentials and an actual local HTTP
    /// endpoint. It is deliberately independent from published-peer acceptance.
    /// </summary>
    private sealed partial class TokenProfileFixture : IAsyncDisposable
    {
        private const int TenantId = 81;
        private const string LocalTenant = "81";
        private const string PeerTenant = "synthetic-organization";
        private const string OutboundScope = "rateldesk.incident-targets.read";
        private const string PeerApi = "https://peer.example.test";
        private readonly WebApplication app;
        private readonly HttpClient client;
        private readonly AsyncServiceScope profileScope;
        private readonly string keyDirectory;
        private readonly ServiceLinkProfileService profiles;
        private readonly ServiceDirectionalCredential outbound;
        private Guid inboundPrincipalId;
        private int peerHttpRequests;
        private int tokenRequests;
        private int invalidTokenRequests;
        private long nextResponseAdvanceTicks;

        private TokenProfileFixture(WebApplication app, HttpClient client, AsyncServiceScope scope,
            string keyDirectory, ProfileClock clock, ServicePublicSettingsEffective settings, ServiceDirectionalCredential outbound)
        {
            this.app = app; this.client = client; profileScope = scope; this.keyDirectory = keyDirectory;
            this.outbound = outbound; Clock = clock; publicSettings = settings;
            profiles = new(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(), new FixturePublicSettings(settings),
                app.Services.GetRequiredService<IDataProtectionProvider>(), new ServiceLinkTransport(client, Options.Create(settings.Linking)),
                app.Services.GetRequiredService<IMemoryCache>(), clock);
        }

        public ProfileClock Clock { get; }
        public ServiceLinkResolvedProfile Profile { get; private set; } = null!;
        public string? ExpiresInJson { get; set; }
        public int PeerHttpRequests => Volatile.Read(ref peerHttpRequests);
        public int TokenRequests => Volatile.Read(ref tokenRequests);
        public int InvalidTokenRequests => Volatile.Read(ref invalidTokenRequests);
        public Task<string> GetTokenAsync(CancellationToken ct = default) => profiles.GetAccessTokenAsync(Profile, OutboundScope, ct);
        public void AdvanceDuringNextResponse(TimeSpan elapsed) => Interlocked.Exchange(ref nextResponseAdvanceTicks, elapsed.Ticks);

        public static async Task<TokenProfileFixture> CreateAsync(PostgreSqlPersistenceFixture postgres, int expiresIn)
        {
            var connection = await postgres.CreateDatabaseAsync();
            var directory = Path.Combine(Path.GetTempPath(), "netratel-profile-expiry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var clock = new ProfileClock();
            var instance = Guid.NewGuid(); var source = Guid.NewGuid(); var peerInstance = Guid.NewGuid();
            var settings = new ServicePublicSettingsEffective(
                new ServiceIdentityOptions { Enabled = true, InstanceId = instance.ToString("D"),
                    WebBaseUrl = "https://web.example.test", ApiBaseUrl = "https://api.example.test",
                    Issuer = "https://api.example.test/services", Audience = "netratel.services" },
                new ServiceLinkOptions { Enabled = true, WebBaseUrl = "https://web.example.test", ApiBaseUrl = "https://api.example.test",
                    GatewayBaseUrl = "https://gateway.example.test", SourceInstanceId = source.ToString("D") }, 1, []);
            Assert.False(new ServiceIdentityOptionsValidator().Validate(null, settings.Identity).Failed);
            Assert.False(new ServiceLinkOptionsValidator(Options.Create(settings.Identity)).Validate(null, settings.Linking).Failed);
            var outbound = new ServiceDirectionalCredential
            {
                ClientId = "synthetic-peer-client", ClientSecret = ServiceLinkValidation.Proof(),
                Issuer = PeerApi + "/services", TokenEndpoint = PeerApi + "/connect/token", Audience = "rateldesk.services",
                Scopes = [OutboundScope], CallerInstanceId = instance.ToString("D"), CallerTenantId = LocalTenant,
                TargetInstanceId = peerInstance.ToString("D"), TargetTenantId = PeerTenant
            };
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connection));
            builder.Services.AddMemoryCache();
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(directory))
                .SetApplicationName("NetRatel.ProfileExpiry.Tests");
            var app = builder.Build();
            TokenProfileFixture? fixture = null;
            app.Use((context, next) =>
            {
                Interlocked.Increment(ref fixture!.peerHttpRequests);
                return next(context);
            });
            app.MapPost("/connect/token", (HttpRequest request) => fixture!.RespondAsync(request));
            try
            {
                await app.StartAsync();
                var client = app.GetTestClient(); client.BaseAddress = new Uri(PeerApi);
                fixture = new(app, client, app.Services.CreateAsyncScope(), directory, clock, settings, outbound)
                    { ExpiresInJson = expiresIn.ToString(CultureInfo.InvariantCulture) };
                await fixture.SeedApprovedProfileAsync(settings, instance, source, peerInstance);
                return fixture;
            }
            catch
            {
                if (fixture is not null) await fixture.DisposeAsync();
                else { await app.DisposeAsync(); Directory.Delete(directory, true); }
                throw;
            }
        }

        private async Task SeedApprovedProfileAsync(ServicePublicSettingsEffective settings, Guid instance, Guid source, Guid peerInstance)
        {
            var db = profileScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.MigrateAsync();
            var now = Clock.GetUtcNow(); var agent = Guid.NewGuid();
            db.Tenants.Add(new() { Id = TenantId, Name = "Synthetic profile tenant", CreatedAtUtc = now, UpdatedAtUtc = now });
            db.Agents.Add(new() { Id = agent, TenantId = TenantId, Name = "Synthetic profile resource", Status = AgentStatus.Active, CreatedAtUtc = now });
            db.Set<ServiceLinkRuntimeIdentity>().Add(new() { InstanceId = instance, SourceInstanceId = source,
                SourceAdoptedBy = "synthetic-administrator", SourceAdoptedAtUnixSeconds = now.ToUnixTimeSeconds() });
            await db.SaveChangesAsync();
            var local = Metadata("netratel", instance.ToString("D"), settings.Identity.WebBaseUrl, settings.Identity.ApiBaseUrl,
                settings.Identity.Issuer, settings.Identity.Audience, source.ToString("D"), settings.Linking.GatewayBaseUrl,
                "orchestration", ServiceIdentityScopes.OrchestrationRead, "/api/internal/orchestration/tenants");
            var peer = Metadata("rateldesk", peerInstance.ToString("D"), "https://peer-web.example.test", PeerApi,
                outbound.Issuer, outbound.Audience, null, null, "incident-delivery", OutboundScope, "/api/v1/incident-targets");
            var inbound = new ServiceLinkGrant
            {
                DirectionId = ServiceLinkContract.ResponderToInitiator, CallerSnapshot = "responder", TargetSnapshot = "initiator",
                CallerProduct = "rateldesk", CallerInstanceId = peer.InstanceId, CallerTenantId = PeerTenant,
                TargetProduct = "netratel", TargetInstanceId = local.InstanceId, TargetTenantId = LocalTenant,
                Issuer = local.OauthIssuer, Audience = local.Audience, Capabilities = ["orchestration"],
                Scopes = [ServiceIdentityScopes.OrchestrationRead],
                ResourceConstraints = new() { TenantId = LocalTenant, ResourceIds = [agent.ToString("D")] }
            };
            var outgoing = new ServiceLinkGrant
            {
                DirectionId = ServiceLinkContract.InitiatorToResponder, CallerSnapshot = "initiator", TargetSnapshot = "responder",
                CallerProduct = "netratel", CallerInstanceId = local.InstanceId, CallerTenantId = LocalTenant,
                TargetProduct = "rateldesk", TargetInstanceId = peer.InstanceId, TargetTenantId = PeerTenant,
                Issuer = peer.OauthIssuer, Audience = peer.Audience, Capabilities = ["incident-delivery"], Scopes = [OutboundScope],
                ResourceConstraints = new() { OrganizationId = PeerTenant, CustomerIds = ["synthetic-customer"] },
                SourceInstanceId = source.ToString("D"), SourceNamespaceId = Guid.NewGuid().ToString("D")
            };
            var summary = new ServiceLinkGrantSummary
            {
                AttemptId = ServiceLinkValidation.NewId(), LinkId = ServiceLinkValidation.NewId(),
                DescriptorHash = ServiceLinkValidation.Digest("synthetic-approved-descriptor"),
                ExpiresAt = ServiceLinkValidation.Timestamp(now.AddMinutes(15).ToUnixTimeSeconds()),
                InitiatorInstanceId = local.InstanceId, ResponderInstanceId = peer.InstanceId,
                InitiatorEndpointSnapshot = ServiceLinkValidation.Metadata(local, false),
                ResponderEndpointSnapshot = ServiceLinkValidation.Metadata(peer, false),
                Grants = ServiceLinkValidation.Grants([outgoing, inbound], local, peer)
            };
            await ServiceLinkGrantAuthority.ValidateLocalGrantAsync(db, inbound, null, null, CancellationToken.None);
            var principal = new ServicePrincipalRegistration
            {
                ClientId = "synthetic-inbound-client", NormalizedClientId = "synthetic-inbound-client", AliasKey = "synthetic-inbound-client",
                Name = "Synthetic current linked principal", TenantId = TenantId, PeerInstanceId = peer.InstanceId, PeerTenantId = PeerTenant,
                AllowedScopesJson = Serialize(inbound.Scopes), ResourceConstraintsJson = Serialize(inbound.ResourceConstraints),
                LinkId = summary.LinkId, AttemptId = summary.AttemptId, GrantHash = ServiceLinkCanonicalJson.HashObject(summary),
                DescriptorHash = summary.DescriptorHash, DirectionId = inbound.DirectionId, Status = "active",
                CreatedBy = "synthetic-administrator", ApprovedBy = "synthetic-administrator", CreatedAtUtc = now, UpdatedAtUtc = now
            };
            inboundPrincipalId = principal.Id;
            db.Set<ServicePrincipalRegistration>().Add(principal);
            db.Set<ServicePrincipalSecret>().Add(new() { ServicePrincipalId = principal.Id, CredentialRevision = 1,
                Status = "active", SecretHash = new string('a', 64), Salt = new string('b', 64), CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(1) });
            var attempt = new ServiceLinkAttempt
            {
                AttemptId = summary.AttemptId, Role = "initiator", LocalTenantId = LocalTenant, LocalActorId = "synthetic-administrator",
                PeerInstanceId = peer.InstanceId, PeerTenantId = PeerTenant, LinkId = summary.LinkId,
                LifecycleState = "active", Decision = "commit", CommitId = ServiceLinkValidation.NewId(),
                DescriptorJson = "{}", DescriptorHash = summary.DescriptorHash, GrantSummaryJson = Serialize(summary), GrantHash = principal.GrantHash,
                ConsentId = ServiceLinkValidation.NewId(), InboundPrincipalId = principal.Id, OutboundProfileRevision = 1,
                LocalInboundActive = true, LocalBusinessSenderEnabled = true, PeerActiveAcknowledged = true,
                LocalActiveAcknowledged = true, PeerPreparedAcknowledged = true, LocalPreparedAcknowledged = true,
                CreatedAtUnixSeconds = now.ToUnixTimeSeconds(), UpdatedAtUnixSeconds = now.ToUnixTimeSeconds(),
                ExpiresAtUnixSeconds = now.AddMinutes(15).ToUnixTimeSeconds()
            };
            attempt.ProtectedOutboundCredential = app.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("NetRatel.ServiceLink.v1", attempt.AttemptId, attempt.PeerInstanceId, "outbound-credential",
                    attempt.LocalTenantId + "/" + attempt.LinkId + "/" + attempt.GrantHash + "/" + attempt.LinkRevision)
                .Protect(Serialize(outbound));
            db.Set<ServiceLinkAttempt>().Add(attempt);
            await db.SaveChangesAsync();
            // Validate against committed rows, not the fixture's tracked seed.
            db.ChangeTracker.Clear();
            Assert.True(await ServiceLinkAuthority.InboundUsableAsync(db, attempt, Clock, settings, CancellationToken.None));
            Profile = await profiles.ResolveAsync(TenantId, summary.LinkId, OutboundScope, CancellationToken.None);
        }

        public async Task RevokeInboundAsync()
        {
            // A separate context makes the cache prove it reads current database
            // authority, rather than retaining the seeded tracked principal.
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var principal = await db.Set<ServicePrincipalRegistration>().SingleAsync(x => x.Id == inboundPrincipalId);
            principal.Status = "revoked"; principal.Version++; principal.Revision++;
            await db.SaveChangesAsync();
        }

        private async Task<IResult> RespondAsync(HttpRequest request)
        {
            var sequence = Interlocked.Increment(ref tokenRequests);
            var form = await request.ReadFormAsync();
            if (form.Count != 4 || form["grant_type"] != "client_credentials" || form["client_id"] != outbound.ClientId ||
                form["client_secret"] != outbound.ClientSecret || form["scope"] != OutboundScope)
            {
                Interlocked.Increment(ref invalidTokenRequests);
                return Results.BadRequest();
            }
            Clock.Advance(TimeSpan.FromTicks(Interlocked.Exchange(ref nextResponseAdvanceTicks, 0)));
            var lifetime = ExpiresInJson is null ? "" : ",\"expires_in\":" + ExpiresInJson;
            var body = "{\"access_token\":\"synthetic-token-" + sequence + "\",\"token_type\":\"Bearer\"" + lifetime + "}";
            var held = Interlocked.Exchange(ref nextHeldResponse, null);
            return held is null ? Results.Text(body, "application/json") : held.Prepare(body);
        }

        private static ServiceLinkMetadata Metadata(string product, string instance, string web, string api, string issuer,
            string audience, string? source, string? gateway, string capability, string scope, string resourcePath) => new()
        {
            Product = product, ProductVersion = "synthetic-fixture", InstanceId = instance, SourceInstanceId = source,
            WebBaseUrl = web, ApiBaseUrl = api, GatewayBaseUrl = gateway, OauthIssuer = issuer, Audience = audience,
            OauthMetadataUrl = api + "/.well-known/oauth-authorization-server", TokenEndpoint = api + "/connect/token",
            JwksUri = api + "/.well-known/service-jwks.json", ServiceLinkEndpoint = api + ServiceLinkContract.EndpointPath,
            ApprovalEndpoint = web + "/account/integration-credentials/link/approve",
            CallbackEndpoint = web + "/account/integration-credentials/link/callback",
            PermissionProfiles = [new(capability, [scope], [new("GET", resourcePath, scope)])]
        };

        private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ServiceLinkCanonicalJson.Json);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref nextHeldResponse, null)?.Release();
            await profileScope.DisposeAsync();
            client.Dispose();
            await app.DisposeAsync();
            Directory.Delete(keyDirectory, true);
        }
    }

    private sealed class FixturePublicSettings(ServicePublicSettingsEffective current) : IServicePublicSettingsResolver
    {
        public Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default) => Task.FromResult(current);
        public Task<ServicePublicSettingsEffective> UpdateAsync(ServicePublicSettingsUpdate update, string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException("The synthetic fixture has fixed explicitly approved public addresses.");
    }

    private sealed class ProfileClock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }
}

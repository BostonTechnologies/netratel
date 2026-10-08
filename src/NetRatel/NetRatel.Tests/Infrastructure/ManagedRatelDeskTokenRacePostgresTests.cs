using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.Contracts.RatelDesk;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class ManagedRatelDeskTokenRacePostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Existing_managed_reference_recovers_failed_and_expired_readiness_without_provisioning_or_business_send()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        await rig.VerifyReadinessRecoveryAsync();
    }

    [Fact]
    public async Task Optional_scoped_tasks_keep_incident_readiness_in_authenticated_bound_status()
    {
        await using var rig = await Rig.CreateAsync(postgres, optionalTasks: true);
        await rig.VerifyReadinessRecoveryAsync(verifyBoundStatus: true);
    }

    [Theory]
    [InlineData("sender-disabled")]
    [InlineData("unlinked")]
    [InlineData("inbound-revoked")]
    [InlineData("owner-disabled")]
    [InlineData("owner-permission-revoked")]
    [InlineData("connector-disabled")]
    [InlineData("connector-mode-changed")]
    [InlineData("connector-binding-changed")]
    [InlineData("connector-revision-changed")]
    [InlineData("approved-semantic-drift")]
    [InlineData("profile-only-revision")]
    public async Task Completed_current_state_change_during_token_HTTP_denies_bearer_and_business_send(string change)
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var send = rig.SendCapturedAsync();
        await rig.WaitForTokenRequestAsync(1);
        await rig.ChangeAsync(change);
        rig.ReleaseTokenResponse(1);

        Func<Task> complete = () => send;
        var error = (await complete.Should().ThrowAsync<Exception>()).Which;
        if (change.StartsWith("owner-", StringComparison.Ordinal) || change.StartsWith("connector-", StringComparison.Ordinal))
            error.Should().BeOfType<UnauthorizedAccessException>();
        else
        {
            var protocol = error.Should().BeOfType<ServiceLinkProtocolException>().Subject;
            protocol.StatusCode.Should().Be(403);
        }
        rig.TokenRequests.Should().Be(1);
        rig.BusinessRequests.Should().Be(0);
        rig.InvalidTokenRequests.Should().Be(0);
        rig.Peer.Should().BeEquivalentTo(rig.OriginalPeer);
        if (!change.StartsWith("owner-", StringComparison.Ordinal) && !change.StartsWith("connector-", StringComparison.Ordinal))
            (await rig.ResolveTokenCacheKeysAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_successor_during_token_HTTP_uses_one_refresh_for_identical_capture_body_and_key(bool reorderEquivalentScopes)
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var send = rig.SendCapturedAsync();
        await rig.WaitForTokenRequestAsync(1);
        await rig.RotateSecretAsync(reorderEquivalentScopes);
        rig.ReleaseTokenResponse(1);
        await rig.WaitForTokenRequestAsync(2);
        rig.ReleaseTokenResponse(2);
        await send;

        rig.TokenRequests.Should().Be(2);
        rig.InvalidTokenRequests.Should().Be(0);
        rig.BusinessRequests.Should().Be(1);
        rig.CredentialRevisionsSent.Should().Equal(1L, 2L);
        rig.BusinessBearer.Should().Be("synthetic-token-2");
        rig.BusinessBody.Should().Be(rig.OriginalBody);
        rig.BusinessKey.Should().Be(rig.OriginalKey);
        rig.Peer.Should().BeEquivalentTo(rig.OriginalPeer);
        (await rig.CurrentConnectorAsync()).Revision.Should().Be(rig.Connector.Revision);
    }

    [Fact]
    public async Task Second_credential_successor_during_refresh_cannot_create_a_third_token_request_or_business_send()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var send = rig.SendCapturedAsync();
        await rig.WaitForTokenRequestAsync(1);
        await rig.RotateSecretAsync();
        rig.ReleaseTokenResponse(1);
        await rig.WaitForTokenRequestAsync(2);
        await rig.RotateSecretAsync();
        rig.ReleaseTokenResponse(2);

        Func<Task> complete = () => send;
        var error = (await complete.Should().ThrowAsync<ServiceLinkProtocolException>()).Which;
        error.StatusCode.Should().Be(409);
        error.Code.Should().Be("profile-revision-conflict");
        rig.TokenRequests.Should().Be(2);
        rig.BusinessRequests.Should().Be(0);
        rig.InvalidTokenRequests.Should().Be(0);
        rig.CredentialRevisionsSent.Should().Equal(1L, 2L);
    }

    [Theory]
    [InlineData("sender-disabled")]
    [InlineData("unlinked")]
    [InlineData("inbound-revoked")]
    [InlineData("approved-semantic-drift")]
    public async Task Foundation_token_consumer_rechecks_durable_profile_after_HTTP_without_managed_wrapper(string change)
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var token = rig.GetProfileTokenAsync();
        await rig.WaitForTokenRequestAsync(1);
        await rig.ChangeAsync(change);
        rig.ReleaseTokenResponse(1);
        Func<Task> complete = () => token;
        (await complete.Should().ThrowAsync<ServiceLinkProtocolException>()).Which.StatusCode.Should().Be(403);
        rig.TokenRequests.Should().Be(1);
        rig.BusinessRequests.Should().Be(0);
        (await rig.ResolveTokenCacheKeysAsync()).Should().BeEmpty();
    }

    // Synthetic approval, real production profile/transport/connector authorization,
    // migrated PostgreSQL and TestServer HTTP. This is not published-peer, Flow
    // attempt-marker or physical-client acceptance. No mocked profile store exists.
    private sealed class Rig : IAsyncDisposable
    {
        private const int TenantId = 81;
        private const string OwnerId = "synthetic-connector-owner";
        private const string LocalTenant = "81";
        private const string PeerTenant = "synthetic-organization";
        private const string PeerApi = "https://peer.example.test";
        private const string Scope = "rateldesk.incidents.create";
        private static readonly string[] ReceiverScopes = [Scope, "rateldesk.incident-receipts.read", "rateldesk.incident-targets.read"];
        private readonly WebApplication app;
        private readonly HttpClient client;
        private readonly AsyncServiceScope receiverScope;
        private readonly string keyDirectory;
        private readonly ServiceLinkProfileService profiles;
        private readonly ManagedRatelDeskBindingResolver binding;
        private readonly MemoryCache cache;
        private readonly TaskCompletionSource[] entered = [Barrier(), Barrier(), Barrier()];
        private readonly TaskCompletionSource[] release = [Barrier(), Barrier(), Barrier()];
        private readonly ConcurrentQueue<long> revisionsSent = new();
        private readonly FixedClock clock;
        private readonly ServicePublicSettingsEffective settings;
        private ServiceDirectionalCredential outbound = null!;
        private ServiceLinkResolvedProfile profile = null!;
        private Guid inboundPrincipal;
        private string attemptId = "";
        private int tokenRequests;
        private int invalidTokenRequests;
        private int businessRequests;
        private bool readinessTokens;

        private Rig(WebApplication app, HttpClient client, string directory, FixedClock clock,
            ServicePublicSettingsEffective settings)
        {
            this.app = app; this.client = client; keyDirectory = directory; this.clock = clock; this.settings = settings;
            receiverScope = app.Services.CreateAsyncScope();
            var services = receiverScope.ServiceProvider;
            cache = (MemoryCache)app.Services.GetRequiredService<IMemoryCache>();
            profiles = new(services.GetRequiredService<OrchestratorDbContext>(), new PublicSettings(settings),
                app.Services.GetRequiredService<IDataProtectionProvider>(), new ServiceLinkTransport(client, Options.Create(settings.Linking)), cache, clock);
            var identity = services.GetRequiredService<NetRatelIdentityDbContext>();
            var authority = new RatelDeskConnectorAuthorization(new EffectiveAccessService(identity, new ConfigurationBuilder().Build()), identity);
            binding = new(profiles, services.GetRequiredService<ServiceLinkIdentityStore>(),
                app.Services.GetRequiredService<FlowPersistenceService>(), authority,
                new RatelDeskConnectorStore(services.GetRequiredService<OrchestratorDbContext>()));
        }

        public RatelDeskConnectorState Connector { get; private set; } = null!;
        public RatelDeskSemanticPeer Peer { get; private set; } = null!;
        public RatelDeskSemanticPeer OriginalPeer { get; private set; } = null!;
        public string OriginalBody { get; private set; } = "";
        public string OriginalKey { get; private set; } = "";
        public string? BusinessBody { get; private set; }
        public string? BusinessKey { get; private set; }
        public string? BusinessBearer { get; private set; }
        public int TokenRequests => Volatile.Read(ref tokenRequests);
        public int InvalidTokenRequests => Volatile.Read(ref invalidTokenRequests);
        public int BusinessRequests => Volatile.Read(ref businessRequests);
        public long[] CredentialRevisionsSent => revisionsSent.ToArray();

        public static async Task<Rig> CreateAsync(PostgreSqlPersistenceFixture postgres, bool optionalTasks = false)
        {
            var connection = await postgres.CreateDatabaseAsync();
            var directory = Path.Combine(Path.GetTempPath(), "netratel-managed-token-race-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var clock = new FixedClock();
            var instance = Guid.NewGuid(); var source = Guid.NewGuid(); var peerInstance = Guid.NewGuid();
            var settings = new ServicePublicSettingsEffective(
                new ServiceIdentityOptions { Enabled = true, InstanceId = instance.ToString("D"), WebBaseUrl = "https://web.example.test",
                    ApiBaseUrl = "https://api.example.test", Issuer = "https://api.example.test/services", Audience = "netratel.services" },
                new ServiceLinkOptions { Enabled = true, WebBaseUrl = "https://web.example.test", ApiBaseUrl = "https://api.example.test",
                    GatewayBaseUrl = "https://gateway.example.test", SourceInstanceId = source.ToString("D") }, 1, []);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseNpgsql(connection));
            builder.Services.AddDbContext<NetRatelIdentityDbContext>(o => o.UseNpgsql(connection));
            builder.Services.AddSingleton(clock);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddMemoryCache();
            builder.Services.AddSingleton<IOptionsMonitor<ServiceIdentityOptions>>(new CurrentOptions<ServiceIdentityOptions>(settings.Identity));
            builder.Services.AddSingleton<IOptionsMonitor<ServiceLinkOptions>>(new CurrentOptions<ServiceLinkOptions>(settings.Linking));
            builder.Services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
            builder.Services.AddScoped<ServiceLinkIdentityStore>();
            builder.Services.AddSingleton<FlowPersistenceService>();
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(directory)).SetApplicationName("NetRatel.ManagedTokenRace.Tests");
            var app = builder.Build();
            Rig? rig = null;
            app.MapPost("/connect/token", (HttpRequest request) => rig!.TokenResponseAsync(request));
            app.MapPost("/api/v1/incidents/", (HttpRequest request) => rig!.BusinessResponseAsync(request));
            try
            {
                await app.StartAsync();
                var client = app.GetTestClient(); client.BaseAddress = new Uri(PeerApi);
                rig = new(app, client, directory, clock, settings);
                await rig.SeedAsync(settings, instance, source, peerInstance, optionalTasks);
                return rig;
            }
            catch
            {
                if (rig is not null) await rig.DisposeAsync();
                else { await app.DisposeAsync(); Directory.Delete(directory, true); }
                throw;
            }
        }

        private async Task SeedAsync(ServicePublicSettingsEffective settings, Guid instance, Guid source, Guid peerInstance, bool optionalTasks)
        {
            var db = receiverScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            // Requires the coordinated append-only receiver migration. Never use EnsureCreated.
            await db.Database.MigrateAsync();
            var identity = receiverScope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            await identity.Database.MigrateAsync();
            var now = clock.GetUtcNow(); var agent = Guid.NewGuid();
            db.Tenants.Add(new() { Id = TenantId, Name = "Synthetic receiver tenant", CreatedAtUtc = now, UpdatedAtUtc = now });
            db.Agents.Add(new() { Id = agent, TenantId = TenantId, Name = "Synthetic current resource", Status = AgentStatus.Active, CreatedAtUtc = now });
            if (optionalTasks) db.Jobs.Add(new() { Id = 901, TenantId = TenantId, AgentId = agent,
                Name = "Explicitly approved harmless task", CreatedAtUtc = now, UpdatedAtUtc = now });
            db.Set<ServiceLinkRuntimeIdentity>().Add(new() { InstanceId = instance, SourceInstanceId = source,
                SourceAdoptedBy = OwnerId, SourceAdoptedAtUnixSeconds = now.ToUnixTimeSeconds() });
            db.FlowRuntimeIdentity.Add(new() { SourceInstanceId = source });
            await db.SaveChangesAsync();
            identity.ApplicationPrincipals.Add(new() { Id = OwnerId, LocalUserId = "synthetic-owner-user" });
            identity.Users.Add(new() { Id = "synthetic-owner-user", PrincipalId = OwnerId, UserName = "synthetic-owner", IsEnabled = true });
            var role = new AccessRole { Name = "Synthetic tenant receiver authority" };
            role.Permissions.Add(new() { RoleId = role.Id, Permission = NetRatelPermissions.IntegrationManagement });
            role.Permissions.Add(new() { RoleId = role.Id, Permission = NetRatelPermissions.SecretUse });
            identity.AccessRoles.Add(role);
            identity.PrincipalRoleAssignments.Add(new() { PrincipalId = OwnerId, RoleId = role.Id, TenantId = TenantId });
            await identity.SaveChangesAsync();
            var local = Metadata("netratel", instance.ToString("D"), settings.Identity.WebBaseUrl, settings.Identity.ApiBaseUrl,
                settings.Identity.Issuer, settings.Identity.Audience, source.ToString("D"), settings.Linking.GatewayBaseUrl,
                [new("orchestration", optionalTasks ? [ServiceIdentityScopes.OrchestrationRead, ServiceIdentityScopes.OrchestrationInvoke] : [ServiceIdentityScopes.OrchestrationRead],
                    [new("GET", "/api/internal/orchestration/tenants", ServiceIdentityScopes.OrchestrationRead)])]);
            var peer = Metadata("rateldesk", peerInstance.ToString("D"), "https://peer-web.example.test", PeerApi,
                PeerApi + "/services", "rateldesk.services", null, null,
                [new("incident-delivery", optionalTasks ? [.. ReceiverScopes, "rateldesk.orchestration.callback"] : ReceiverScopes, [new("POST", "/api/v1/incidents/", ReceiverScopes[0]),
                    new("GET", "/api/v1/integrations/netratel/incident-receipts/{key}", ReceiverScopes[1]),
                    new("POST", "/api/v1/integrations/netratel/targets/validate", ReceiverScopes[2])])]);
            var inbound = new ServiceLinkGrant
            {
                DirectionId = ServiceLinkContract.ResponderToInitiator, CallerSnapshot = "responder", TargetSnapshot = "initiator",
                CallerProduct = "rateldesk", CallerInstanceId = peer.InstanceId, CallerTenantId = PeerTenant,
                TargetProduct = "netratel", TargetInstanceId = local.InstanceId, TargetTenantId = LocalTenant,
                Issuer = local.OauthIssuer, Audience = local.Audience, Capabilities = ["orchestration"],
                Scopes = optionalTasks ? [ServiceIdentityScopes.OrchestrationRead, ServiceIdentityScopes.OrchestrationInvoke] : [ServiceIdentityScopes.OrchestrationRead],
                ResourceConstraints = new() { TenantId = LocalTenant, ResourceIds = [agent.ToString("D")], RequestDefinitionIds = optionalTasks ? ["901"] : [] }
            };
            var outgoing = new ServiceLinkGrant
            {
                DirectionId = ServiceLinkContract.InitiatorToResponder, CallerSnapshot = "initiator", TargetSnapshot = "responder",
                CallerProduct = "netratel", CallerInstanceId = local.InstanceId, CallerTenantId = LocalTenant,
                TargetProduct = "rateldesk", TargetInstanceId = peer.InstanceId, TargetTenantId = PeerTenant,
                Issuer = peer.OauthIssuer, Audience = peer.Audience, Capabilities = ["incident-delivery"],
                Scopes = optionalTasks ? [.. ReceiverScopes, "rateldesk.orchestration.callback"] : ReceiverScopes,
                ResourceConstraints = new() { OrganizationId = PeerTenant, CustomerIds = ["synthetic-customer"] },
                SourceInstanceId = source.ToString("D"), SourceNamespaceId = Guid.NewGuid().ToString("D")
            };
            var summary = new ServiceLinkGrantSummary
            {
                AttemptId = ServiceLinkValidation.NewId(), LinkId = ServiceLinkValidation.NewId(),
                DescriptorHash = ServiceLinkValidation.Digest("synthetic-approved-receiver"),
                ExpiresAt = ServiceLinkValidation.Timestamp(now.AddMinutes(15).ToUnixTimeSeconds()),
                InitiatorInstanceId = local.InstanceId, ResponderInstanceId = peer.InstanceId,
                InitiatorEndpointSnapshot = ServiceLinkValidation.Metadata(local, false), ResponderEndpointSnapshot = ServiceLinkValidation.Metadata(peer, false),
                Grants = ServiceLinkValidation.Grants([outgoing, inbound], local, peer)
            };
            await ServiceLinkGrantAuthority.ValidateLocalGrantAsync(db, inbound, null, null, CancellationToken.None);
            var principal = new ServicePrincipalRegistration
            {
                ClientId = "synthetic-inbound-client", NormalizedClientId = "synthetic-inbound-client", AliasKey = "synthetic-inbound-client",
                Name = "Synthetic active linked principal", TenantId = TenantId, PeerInstanceId = peer.InstanceId, PeerTenantId = PeerTenant,
                AllowedScopesJson = Serialize(inbound.Scopes), ResourceConstraintsJson = Serialize(inbound.ResourceConstraints),
                LinkId = summary.LinkId, AttemptId = summary.AttemptId, GrantHash = ServiceLinkCanonicalJson.HashObject(summary),
                DescriptorHash = summary.DescriptorHash, DirectionId = inbound.DirectionId, Status = "active",
                CreatedBy = OwnerId, ApprovedBy = OwnerId, CreatedAtUtc = now, UpdatedAtUtc = now
            };
            inboundPrincipal = principal.Id;
            db.Set<ServicePrincipalRegistration>().Add(principal);
            db.Set<ServicePrincipalSecret>().Add(new() { ServicePrincipalId = principal.Id, CredentialRevision = 1, Status = "active",
                SecretHash = new string('a', 64), Salt = new string('b', 64), CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(1) });
            var attempt = new ServiceLinkAttempt
            {
                AttemptId = summary.AttemptId, Role = "initiator", LocalTenantId = LocalTenant, LocalActorId = OwnerId,
                PeerInstanceId = peer.InstanceId, PeerTenantId = PeerTenant, LinkId = summary.LinkId,
                LifecycleState = "active", Decision = "commit", CommitId = ServiceLinkValidation.NewId(),
                DescriptorJson = "{}", DescriptorHash = summary.DescriptorHash, GrantSummaryJson = Serialize(summary), GrantHash = principal.GrantHash,
                ConsentId = ServiceLinkValidation.NewId(), InboundPrincipalId = principal.Id, OutboundProfileRevision = 1,
                LocalInboundActive = true, LocalBusinessSenderEnabled = true, PeerActiveAcknowledged = true, LocalActiveAcknowledged = true,
                PeerPreparedAcknowledged = true, LocalPreparedAcknowledged = true, CreatedAtUnixSeconds = now.ToUnixTimeSeconds(),
                UpdatedAtUnixSeconds = now.ToUnixTimeSeconds(), ExpiresAtUnixSeconds = now.AddMinutes(15).ToUnixTimeSeconds()
            };
            outbound = new()
            {
                ClientId = "synthetic-peer-client", ClientSecret = ServiceLinkValidation.Proof(), Issuer = peer.OauthIssuer,
                TokenEndpoint = peer.TokenEndpoint, Audience = peer.Audience, Scopes = outgoing.Scopes,
                CallerInstanceId = local.InstanceId, CallerTenantId = LocalTenant, TargetInstanceId = peer.InstanceId, TargetTenantId = PeerTenant
            };
            attempt.ProtectedOutboundCredential = Protect(attempt, outbound); attemptId = attempt.AttemptId;
            db.Set<ServiceLinkAttempt>().Add(attempt);
            Connector = new(Guid.NewGuid(), TenantId, 1, 1, OwnerId,
                new("Synthetic managed receiver", PeerApi, PeerTenant, "synthetic-customer", null, [], new(), true), null, 0,
                new(RatelDeskAuthenticationMode.ManagedServiceLink, summary.LinkId));
            db.RatelDeskConnectors.Add(new() { TenantId = TenantId, Id = Connector.Id, Revision = 1, RowVersion = 1, OwnerPrincipalId = OwnerId,
                ConfigurationJson = JsonSerializer.Serialize(Connector.Configuration), AuthenticationJson = JsonSerializer.Serialize(Connector.Authentication) });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear(); identity.ChangeTracker.Clear();
            profile = await profiles.ResolveAsync(TenantId, summary.LinkId, Scope, CancellationToken.None);
            Peer = await binding.CaptureAsync(Connector, Connector.Authentication!, source, CancellationToken.None);
            OriginalPeer = Peer with { CategoryIds = [.. Peer.CategoryIds] };
            OriginalBody = JsonSerializer.Serialize(new RatelDeskCreateIncidentDto("Immutable incident", "Immutable body", 1,
                Connector.Configuration.CustomerId, Connector.Configuration.OrganizationId, null, []));
            // The canonical key golden vector is covered by receiver conformance tests.
            OriginalKey = "8ba5abac281f0a20cf5034acd7e1e1a6e4ac703ff567ab90d105aadf4488d683";
        }

        public Task<string> GetProfileTokenAsync() => profiles.GetAccessTokenAsync(profile, Scope, CancellationToken.None);

        public async Task VerifyReadinessRecoveryAsync(bool verifyBoundStatus = false)
        {
            readinessTokens = true;
            var services = receiverScope.ServiceProvider;
            var db = services.GetRequiredService<OrchestratorDbContext>();
            var identity = services.GetRequiredService<NetRatelIdentityDbContext>();
            var authority = new RatelDeskConnectorAuthorization(new EffectiveAccessService(identity, new ConfigurationBuilder().Build()), identity);
            var store = new RatelDeskConnectorStore(db);
            var installed = services.GetRequiredService<ServiceLinkIdentityStore>();
            var flow = services.GetRequiredService<FlowPersistenceService>();
            var network = new RatelDeskReceiverNetworkPolicy(new CurrentOptions<RatelDeskReceiverOptions>(new()),
                new CurrentOptions<ServiceLinkOptions>(settings.Linking), new CurrentOptions<ServiceIdentityOptions>(settings.Identity));
            var continuity = new RatelDeskProducerContinuity(flow, new RatelDeskInstallationIdentityReader(installed));
            var readiness = new RatelDeskConnectorReadiness(authority, store, continuity, profiles, network, clock);
            var reads = new ReadinessTransport(clock);
            var receiver = new RatelDeskConnectorReceiver(store, store, store, readiness, binding, reads, flow, network, clock);
            var setup = new RatelDeskConnectorSetupService(authority, flow, installed, profiles, db, store, receiver);
            var link = Connector.Authentication!.ManagedLinkId!;
            var original = await CurrentConnectorAsync();
            var originalCredential = (await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync()).ProtectedOutboundCredential;

            (await setup.IsApprovedReferenceReadyAsync(TenantId, link, default)).Should().BeFalse();
            (await CurrentConnectorAsync()).Readiness.Should().BeNull();
            reads.Unavailable = false;
            (await setup.IsApprovedReferenceReadyAsync(TenantId, link, default)).Should().BeTrue();
            reads.CapabilityReads.Should().Be(2); reads.TargetReads.Should().Be(1);
            // Fresh status/catalog observations remain local. Expiry is driven by the existing virtual clock.
            (await setup.IsApprovedReferenceReadyAsync(TenantId, link, default)).Should().BeTrue();
            reads.CapabilityReads.Should().Be(2);
            clock.Advance(RatelDeskConnectorReadiness.MaximumObservationAge + TimeSpan.FromSeconds(1));
            (await readiness.CurrentAsync(await CurrentConnectorAsync(), default)).Code.Should().Be("receiver-readiness-expired");
            (await setup.IsApprovedReferenceReadyAsync(TenantId, link, default)).Should().BeTrue();
            reads.CapabilityReads.Should().Be(3); reads.TargetReads.Should().Be(2);
            if (verifyBoundStatus) await VerifyBoundIncidentStatusAsync(setup, db);
            await ChangeAsync("owner-disabled");
            (await setup.IsApprovedReferenceReadyAsync(TenantId, link, default)).Should().BeFalse();
            reads.CapabilityReads.Should().Be(3);

            var after = await CurrentConnectorAsync();
            after.Id.Should().Be(original.Id); after.Revision.Should().Be(original.Revision);
            after.CredentialRevision.Should().Be(original.CredentialRevision); after.Authentication.Should().Be(original.Authentication);
            (await db.RatelDeskConnectors.CountAsync()).Should().Be(1);
            (await db.Set<ServicePrincipalRegistration>().CountAsync()).Should().Be(1);
            (await db.Set<ServicePrincipalSecret>().CountAsync()).Should().Be(1);
            Assert.True((await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync()).ProtectedOutboundCredential == originalCredential,
                "Readiness must preserve the existing protected credential.");
            BusinessRequests.Should().Be(0); reads.BusinessRequests.Should().Be(0); InvalidTokenRequests.Should().Be(0);
        }

        private async Task VerifyBoundIncidentStatusAsync(IRatelDeskConnectorSetupService setup, OrchestratorDbContext db)
        {
            var principal = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleAsync();
            var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync();
            var summary = ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(attempt.GrantSummaryJson!);
            var reverse = summary.Grants.Single(g => g.TargetProduct == "netratel");
            reverse.Scopes.Should().Contain(ServiceIdentityScopes.OrchestrationInvoke);
            reverse.ResourceConstraints.RequestDefinitionIds.Should().Equal("901");
            summary.Grants.Single(g => g.TargetProduct == "rateldesk").Scopes.Should().Contain("rateldesk.orchestration.callback");
            attempt.DescriptorJson = Serialize(new ServiceLinkRequestDescriptor { InitiatorInstanceId = summary.InitiatorInstanceId,
                InitiatorEndpointSnapshot = summary.InitiatorEndpointSnapshot, ResponderEndpointSnapshot = summary.ResponderEndpointSnapshot });
            await db.SaveChangesAsync();
            var caller = new ClaimsPrincipal(new ClaimsIdentity([
                new("auth_mode", "service"), new("token_use", ServiceIdentityClaims.Purpose),
                new(ServiceIdentityClaims.PrincipalId, principal.Id.ToString("N")), new("sub", $"service:{principal.Id:N}"),
                new("client_id", principal.ClientId), new(ServiceIdentityClaims.CredentialRevision, "1"),
                new(ServiceIdentityClaims.GrantRevision, principal.Revision.ToString()), new(ServiceIdentityClaims.TenantId, LocalTenant),
                new(ServiceIdentityClaims.PeerInstanceId, principal.PeerInstanceId), new(ServiceIdentityClaims.PeerTenantId, principal.PeerTenantId),
                new(ServiceIdentityClaims.LinkId, principal.LinkId!), new(ServiceIdentityClaims.AttemptId, principal.AttemptId!),
                new(ServiceIdentityClaims.GrantHash, principal.GrantHash!), new(ServiceIdentityClaims.DirectionId, principal.DirectionId!),
                new(ServiceIdentityClaims.LinkRevision, principal.LinkRevision.ToString()), new("scope", ServiceLinkContract.ControlScope)
            ], "ServiceFixture"));
            var publicSettings = new PublicSettings(settings);
            var registry = new ServicePrincipalRegistry(db, new ServiceIdentityRuntimeOptions(publicSettings),
                new CurrentOptions<ServiceIdentityOptions>(settings.Identity), publicSettings, new EmptyServiceClientDeploymentCatalog(), clock);
            var coordinator = new ServiceLinkCoordinator(db, registry, null!, profiles,
                new ServiceLinkTransport(client, Options.Create(settings.Linking)), app.Services.GetRequiredService<IDataProtectionProvider>(),
                Options.Create(settings.Linking), publicSettings, clock, null!, connectorSetup: setup);
            var status = (Dictionary<string, object?>)await coordinator.StatusAsync(principal.LinkId!, caller, default);
            status["local_business_sender_enabled"].Should().Be(true);
            status["incident_delivery_ready"].Should().Be(true);
        }

        public async Task SendCapturedAsync()
        {
            var bearer = await binding.GetBearerAsync(Connector, Peer, Scope, CancellationToken.None);
            using var request = new HttpRequestMessage(HttpMethod.Post, PeerApi + "/api/v1/incidents/");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Add("Idempotency-Key", OriginalKey);
            request.Content = new StringContent(OriginalBody, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        public Task WaitForTokenRequestAsync(int request) => entered[request - 1].Task.WaitAsync(TimeSpan.FromSeconds(10));
        public void ReleaseTokenResponse(int request) => release[request - 1].TrySetResult();
        public Task<string[]> ResolveTokenCacheKeysAsync() => Task.FromResult(cache.Keys.OfType<string>()
            .Where(key => key.StartsWith("netratel-service/", StringComparison.Ordinal)).ToArray());
        public async Task<RatelDeskConnectorState> CurrentConnectorAsync()
        {
            await using var scope = app.Services.CreateAsyncScope();
            return (await new RatelDeskConnectorStore(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>())
                .GetAsync(TenantId, Connector.Id, CancellationToken.None))!;
        }

        public async Task RotateSecretAsync(bool reorderEquivalentScopes = false)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync(x => x.AttemptId == attemptId);
            var successor = outbound with { ClientSecret = ServiceLinkValidation.Proof(), CredentialRevision = outbound.CredentialRevision + 1,
                Scopes = reorderEquivalentScopes ? outbound.Scopes.Reverse().ToArray() : outbound.Scopes };
            attempt.ProtectedOutboundCredential = Protect(attempt, successor);
            // Matches the committed StageAsync + SetSenderAsync rotation switch.
            attempt.OutboundProfileRevision += 2; attempt.Revision++;
            await db.SaveChangesAsync();
            outbound = successor;
        }

        public async Task ChangeAsync(string change)
        {
            await using var scope = app.Services.CreateAsyncScope();
            if (change.StartsWith("owner-", StringComparison.Ordinal))
            {
                var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
                if (change == "owner-disabled")
                {
                    var owner = await identity.Users.SingleAsync(x => x.PrincipalId == OwnerId);
                    owner.IsEnabled = false; owner.AuthorizationRevision++;
                }
                else identity.PrincipalRoleAssignments.Remove(await identity.PrincipalRoleAssignments.SingleAsync(x => x.PrincipalId == OwnerId));
                await identity.SaveChangesAsync(); return;
            }
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            if (change.StartsWith("connector-", StringComparison.Ordinal))
            {
                var store = new RatelDeskConnectorStore(db);
                var current = (await store.GetAsync(TenantId, Connector.Id, CancellationToken.None))!;
                var next = current with { Revision = current.Revision + 1, RowVersion = current.RowVersion + 1 };
                next = change switch
                {
                    "connector-disabled" => next with { Configuration = next.Configuration with { Enabled = false } },
                    "connector-mode-changed" => next with { Authentication = new(RatelDeskAuthenticationMode.ManualApiBearer, null) },
                    "connector-binding-changed" => next with { Authentication = new(RatelDeskAuthenticationMode.ManagedServiceLink, "different-approved-link") },
                    _ => next with { Configuration = next.Configuration with { CustomerId = "different-customer" } }
                };
                (await store.SaveAsync(next, current.RowVersion, CancellationToken.None)).Should().BeTrue(); return;
            }
            var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync(x => x.AttemptId == attemptId);
            switch (change)
            {
                case "sender-disabled": attempt.LocalBusinessSenderEnabled = false; attempt.OutboundProfileRevision++; break;
                case "unlinked":
                    attempt.LifecycleState = "revocation_pending"; attempt.LocalBusinessSenderEnabled = false;
                    attempt.LocalInboundActive = false; attempt.OutboundProfileRevision++; break;
                case "inbound-revoked":
                    var principal = await db.Set<ServicePrincipalRegistration>().SingleAsync(x => x.Id == inboundPrincipal);
                    principal.Status = "revoked"; principal.Version++; principal.Revision++; break;
                case "approved-semantic-drift":
                    var summary = ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(attempt.GrantSummaryJson!);
                    var changed = summary with { ProposedLinkRevision = summary.ProposedLinkRevision + 1,
                        Grants = summary.Grants.Select(g => g.DirectionId == ServiceLinkContract.InitiatorToResponder
                            ? g with { ResourceConstraints = g.ResourceConstraints with { CustomerIds = ["different-customer"] } } : g).ToArray() };
                    // A self-consistent synthetic successor approval remains currently usable.
                    // Its changed target must still deny the original captured work.
                    attempt.LinkRevision = changed.ProposedLinkRevision;
                    attempt.GrantSummaryJson = Serialize(changed); attempt.GrantHash = ServiceLinkCanonicalJson.HashObject(changed);
                    attempt.OutboundProfileRevision++;
                    var approvedPrincipal = await db.Set<ServicePrincipalRegistration>().SingleAsync(x => x.Id == inboundPrincipal);
                    approvedPrincipal.LinkRevision = attempt.LinkRevision; approvedPrincipal.GrantHash = attempt.GrantHash;
                    approvedPrincipal.Version++; approvedPrincipal.Revision++;
                    attempt.ProtectedOutboundCredential = Protect(attempt, outbound);
                    break;
                case "profile-only-revision": attempt.OutboundProfileRevision++; break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
            attempt.Revision++; await db.SaveChangesAsync();
            if (change == "approved-semantic-drift")
            {
                // Validate in a different context while the token response is still held.
                await using var check = app.Services.CreateAsyncScope();
                var durable = check.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var currentProfile = new ServiceLinkProfileService(durable, new PublicSettings(settings),
                    app.Services.GetRequiredService<IDataProtectionProvider>(), new ServiceLinkTransport(client,
                        Options.Create(app.Services.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>().CurrentValue)), cache, clock);
                (await currentProfile.ResolveAsync(TenantId, attempt.LinkId!, Scope, CancellationToken.None)).LinkRevision.Should().Be(attempt.LinkRevision);
            }
        }

        private async Task<IResult> TokenResponseAsync(HttpRequest request)
        {
            var index = Interlocked.Increment(ref tokenRequests);
            if (!readinessTokens && index > release.Length) return Results.StatusCode(500);
            var expected = outbound;
            var form = await request.ReadFormAsync();
            if (form.Count != 4 || form["grant_type"] != "client_credentials" || form["client_id"] != expected.ClientId ||
                form["client_secret"] != expected.ClientSecret ||
                (readinessTokens ? !ReceiverScopes.Contains(form["scope"].ToString(), StringComparer.Ordinal) : form["scope"] != Scope))
            {
                Interlocked.Increment(ref invalidTokenRequests); return Results.BadRequest();
            }
            revisionsSent.Enqueue(expected.CredentialRevision);
            if (readinessTokens) return Results.Json(new { access_token = "synthetic-token-" + index, token_type = "Bearer", expires_in = 900 });
            entered[index - 1].TrySetResult();
            await release[index - 1].Task.WaitAsync(request.HttpContext.RequestAborted);
            return Results.Json(new { access_token = "synthetic-token-" + index, token_type = "Bearer", expires_in = 900 });
        }

        private async Task<IResult> BusinessResponseAsync(HttpRequest request)
        {
            Interlocked.Increment(ref businessRequests);
            BusinessBearer = request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal);
            BusinessKey = request.Headers["Idempotency-Key"].ToString();
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            BusinessBody = await reader.ReadToEndAsync();
            return Results.Ok();
        }

        private string Protect(ServiceLinkAttempt attempt, ServiceDirectionalCredential credential) => app.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("NetRatel.ServiceLink.v1", attempt.AttemptId, attempt.PeerInstanceId, "outbound-credential",
                attempt.LocalTenantId + "/" + attempt.LinkId + "/" + attempt.GrantHash + "/" + attempt.LinkRevision).Protect(Serialize(credential));
        private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ServiceLinkCanonicalJson.Json);
        private static ServiceLinkMetadata Metadata(string product, string instance, string web, string api, string issuer,
            string audience, string? source, string? gateway, ServiceLinkPermissionProfile[] profiles) => new()
        {
            Product = product, ProductVersion = "synthetic-fixture", InstanceId = instance, SourceInstanceId = source,
            WebBaseUrl = web, ApiBaseUrl = api, GatewayBaseUrl = gateway, OauthIssuer = issuer, Audience = audience,
            OauthMetadataUrl = api + "/.well-known/oauth-authorization-server", TokenEndpoint = api + "/connect/token",
            JwksUri = api + "/.well-known/service-jwks.json", ServiceLinkEndpoint = api + ServiceLinkContract.EndpointPath,
            ApprovalEndpoint = web + "/account/integration-credentials/link/approve", CallbackEndpoint = web + "/account/integration-credentials/link/callback",
            PermissionProfiles = profiles
        };
        public async ValueTask DisposeAsync()
        {
            foreach (var barrier in release) barrier.TrySetResult();
            await receiverScope.DisposeAsync(); client.Dispose(); await app.DisposeAsync(); Directory.Delete(keyDirectory, true);
        }
    }

    private sealed class PublicSettings(ServicePublicSettingsEffective settings) : IServicePublicSettingsResolver
    {
        public Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default) => Task.FromResult(settings);
        public Task<ServicePublicSettingsEffective> UpdateAsync(ServicePublicSettingsUpdate update, string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException("The fixture has fixed synthetic approved public settings.");
    }
    private sealed class CurrentOptions<T>(T current) : IOptionsMonitor<T>
    {
        public T CurrentValue => current; public T Get(string? name) => current;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset current = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => current;
        internal void Advance(TimeSpan delta) => current += delta;
    }
    private sealed class ReadinessTransport(TimeProvider clock) : IRatelDeskReceiverTransport
    {
        internal bool Unavailable = true;
        internal int CapabilityReads, TargetReads, BusinessRequests;
        public Task<RatelDeskVerifiedCapability> CapabilitiesAsync(RatelDeskSemanticPeer peer, string bearer, CancellationToken ct)
        {
            CapabilityReads++;
            if (Unavailable) throw new RatelDeskReceiverReadException("receiver-unavailable", 503, null);
            return Task.FromResult(new RatelDeskVerifiedCapability(ReceiverWireValidation.Contract, peer.ReceiverInstanceId,
                peer.SourceInstanceId, peer.SourceNamespaceId, new(peer.ApiBaseUrl + ReceiverWireValidation.CapabilitiesPath,
                    peer.ApiBaseUrl + ReceiverWireValidation.CreatePath, peer.ApiBaseUrl + ReceiverWireValidation.ReceiptPath,
                    peer.ApiBaseUrl + ReceiverWireValidation.TargetsPath), 7776000, 2592000, clock.GetUtcNow()));
        }
        public Task ValidateTargetsAsync(RatelDeskSemanticPeer peer, RatelDeskVerifiedCapability capability, string bearer, CancellationToken ct)
        { TargetReads++; return Task.CompletedTask; }
        public Task<RatelDeskReceiverObservation> LookupAsync(RatelDeskReceiverPreparationV2 prepared, string bearer, CancellationToken ct)
        { BusinessRequests++; throw new InvalidOperationException("unexpected-receipt-read"); }
        public Task<RatelDeskReceiverObservation> CreateAsync(RatelDeskReceiverPreparationV2 prepared, string bearer, CancellationToken ct)
        { BusinessRequests++; throw new InvalidOperationException("unexpected-incident-create"); }
    }
}

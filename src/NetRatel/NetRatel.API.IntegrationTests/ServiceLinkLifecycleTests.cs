using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServiceLinkRealPeerCollection
{
    public const string Name = "Published RatelDesk reciprocal service links";
}

[Collection(ServiceLinkRealPeerCollection.Name)]
public sealed class ServiceLinkLifecycleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_products_require_exact_consent_then_activate_real_grants_and_isolate_human_authority(bool netRatelInitiates)
    {
        await using var pair = await ServiceLinkPair.CreateAsync(netRatelInitiates);
        await pair.PrepareAsync();
        Assert.Equal(0, await LocalCountAsync<ServicePrincipalRegistration>(pair));
        if (netRatelInitiates)
        {
            var continued = await ServiceLinkPair.PostAsync<ServiceLinkNavigation>(pair.NetRatel.Administrator,
                $"/api/v1/admin/service-links/attempts/{pair.Start.AttemptId}/continue", new ServiceLinkContinueRequest(pair.SessionBinding));
            Assert.True(string.Equals(pair.Start.NavigationUrl, continued.NavigationUrl, StringComparison.Ordinal), "The resumed approval navigation differs from its original pinned descriptor.");
            using var foreignSession = await pair.NetRatel.AdminAsync($"/api/v1/admin/service-links/attempts/{pair.Start.AttemptId}/continue", new ServiceLinkContinueRequest(new string('z', 64)));
            Assert.Equal(HttpStatusCode.Forbidden, foreignSession.StatusCode);
            Assert.Equal(0, await LocalCountAsync<ServicePrincipalRegistration>(pair));
        }
        if (!netRatelInitiates)
        {
            var state = QueryHelpers.ParseQuery(new Uri(pair.Start.NavigationUrl).Query)["browser_state"].ToString();
            using var replayedReview = await pair.NetRatel.AdminAsync("/api/v1/admin/service-links/remote-review",
                new ServiceLinkRemoteReviewRequest(pair.RatelDesk.WebBaseUrl, pair.Start.AttemptId, state) { SessionBinding = new string('z', 64) });
            Assert.Equal(HttpStatusCode.Conflict, replayedReview.StatusCode);
            using var wrongApproval = await pair.NetRatel.AdminAsync("/api/v1/admin/service-links/remote-approve",
                new ServiceLinkRemoteApproveRequest(pair.Start.AttemptId, pair.NetRatel.TenantId, pair.Descriptor.RequestedGrants, state) { SessionBinding = new string('z', 64) });
            Assert.Equal(HttpStatusCode.Forbidden, wrongApproval.StatusCode);
            Assert.Equal(0, await LocalCountAsync<ServicePrincipalRegistration>(pair));
        }
        await pair.ReviewAsync();
        var before = await pair.StatusAsync(pair.Initiator);
        Assert.False(before.LocalInboundReady); Assert.False(before.LocalInboundActive); Assert.False(before.LocalBusinessSenderEnabled);
        Assert.Equal(netRatelInitiates ? 0 : 1, await LocalCountAsync<ServicePrincipalRegistration>(pair));
        using (var wrong = await pair.Initiator.PostAsJsonAsync($"/api/v1/admin/service-links/attempts/{pair.Start.AttemptId}/approve",
                   new ServiceLinkLocalApproveRequest(pair.Review.GrantHash, new string('x', 64))))
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        await pair.ApproveAsync();
        Assert.Equal(1, await LocalCountAsync<ServicePrincipalRegistration>(pair));
        await pair.FinishAsync();
        var nr = await pair.StatusAsync(pair.NetRatel.Administrator); var rd = await pair.StatusAsync(pair.RatelDesk.Administrator);
        Assert.Equal(nr.LinkId, rd.LinkId); Assert.Equal(nr.GrantHash, rd.GrantHash); Assert.Equal(nr.CommitId, rd.CommitId);
        Assert.True(nr.LocalInboundActive && nr.LocalBusinessSenderEnabled && nr.PeerActiveAcknowledged);
        Assert.True(rd.LocalInboundActive && rd.LocalBusinessSenderEnabled && rd.PeerActiveAcknowledged);
        await AssertLinkedPrincipalRequiresUnlinkAsync(pair);
        Assert.Equal(pair.NetRatel.SourceInstanceId.ToString("D"), nr.GrantSummary!.Grants.Single(g => g.TargetProduct == "rateldesk").SourceInstanceId);
        Assert.NotNull(nr.GrantSummary.Grants.Single(g => g.TargetProduct == "rateldesk").SourceNamespaceId);
        var token = await pair.TokenAsync(true, "netratel.orchestration.read");
        Assert.Equal(HttpStatusCode.OK, await ServiceLinkPair.GetWithTokenAsync(pair.NetRatel.Anonymous, "/api/v1/system/m2m/ping", token));
        Assert.Equal(HttpStatusCode.OK, await ServiceLinkPair.GetWithTokenAsync(pair.NetRatel.Anonymous, "/internal/health", token));
        foreach (var path in new[] { "/api/v2/access/self", "/api/v2/access/tenants", "/api/v2/account/service-clients", "/api/v1/admin/service-links" })
            Assert.Contains(await ServiceLinkPair.GetWithTokenAsync(pair.NetRatel.Anonymous, path, token), new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        await AssertHumanIsolationAsync(pair, token);
        await AssertCatalogBoundAsync(pair, token);
        var principalIds = await LocalIdsAsync<ServicePrincipalRegistration>(pair, p => p.Id);
        var jwks = await pair.NetRatel.Anonymous.GetStringAsync("/.well-known/service-jwks.json");
        await pair.NetRatel.RestartAsync(); await pair.RatelDesk.RestartAsync();
        Assert.Equal(jwks, await pair.NetRatel.Anonymous.GetStringAsync("/.well-known/service-jwks.json"));
        Assert.Equal(principalIds, await LocalIdsAsync<ServicePrincipalRegistration>(pair, p => p.Id));
        var identity = await pair.NetRatel.Administrator.GetFromJsonAsync<ServiceLinkIdentityDto>("/api/v1/admin/service-links/identity");
        Assert.Equal(pair.NetRatel.InstanceId.ToString("D"), identity!.InstanceId);
        Assert.Equal(pair.NetRatel.SourceInstanceId.ToString("D"), identity.SourceInstanceId);
        Assert.Equal(HttpStatusCode.OK, await ServiceLinkPair.GetWithTokenAsync(pair.NetRatel.Anonymous, "/internal/health", token));
        Assert.True((await pair.StatusAsync(pair.NetRatel.Administrator)).LocalBusinessSenderEnabled);
        await AssertActualReceiverReplayAsync(pair);
    }

    private static async Task AssertLinkedPrincipalRequiresUnlinkAsync(ServiceLinkPair pair)
    {
        Guid id; long revision; long version;
        await using (var before = pair.NetRatel.Services.CreateAsyncScope())
        {
            var db = before.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var principal = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleAsync();
            Assert.NotNull(principal.LinkId); Assert.Equal("active", principal.Status);
            id = principal.Id; revision = principal.Revision; version = principal.Version;
        }
        using var rejected = await pair.NetRatel.Administrator.PostAsJsonAsync(
            $"/api/v2/account/service-clients/{id:D}/revoke", new ServiceClientRevokeRequest(revision));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        using var error = await rejected.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("use-reciprocal-workflow", error!.RootElement.GetProperty("error").GetString());
        await using var after = pair.NetRatel.Services.CreateAsyncScope();
        var current = after.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var unchanged = await current.Set<ServicePrincipalRegistration>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("active", unchanged.Status); Assert.Equal(revision, unchanged.Revision); Assert.Equal(version, unchanged.Version);
        var link = await current.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
        Assert.Equal("active", link.LifecycleState); Assert.Null(link.RevocationId);
        Assert.True(link.LocalInboundActive && link.LocalBusinessSenderEnabled);
        Assert.False(await current.Set<ServiceLinkOperation>().AnyAsync(x => x.Kind == "revoke"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_committed_exchange_response_recovers_after_both_actual_products_restart_without_duplicate_clients(bool netRatelInitiates)
    {
        await using var pair = await ServiceLinkPair.CreateAsync(netRatelInitiates);
        await pair.PrepareAsync(); await pair.ReviewAsync(); await pair.ApproveAsync();
        pair.ResponderProxy.LoseNextCompletedResponse("/exchange");
        await pair.ResumeAsync(pair.Initiator);
        Assert.Equal(1, pair.ResponderProxy.LostResponses);
        var responder = await pair.StatusAsync(pair.Responder);
        Assert.True(responder.LocalOutboundPersisted); Assert.False(responder.LocalBusinessSenderEnabled);
        Assert.Equal(1, await LocalCountAsync<ServicePrincipalRegistration>(pair));
        Assert.Equal(1, await pair.RatelDesk.CountAsync("ServicePrincipalRegistrations"));
        var localId = (await LocalIdsAsync<ServicePrincipalRegistration>(pair, p => p.Id)).Single();
        await pair.NetRatel.RestartAsync(); await pair.RatelDesk.RestartAsync();
        await pair.FinishAsync();
        Assert.Equal(localId, (await LocalIdsAsync<ServicePrincipalRegistration>(pair, p => p.Id)).Single());
        Assert.Equal(1, await pair.RatelDesk.CountAsync("ServicePrincipalRegistrations"));
        Assert.Equal(1, await pair.RatelDesk.CountAsync("IncidentReceiverSources"));
        Assert.Equal(HttpStatusCode.OK, await ServiceLinkPair.GetWithTokenAsync(pair.NetRatel.Anonymous, "/internal/health", await pair.TokenAsync(true, "netratel.orchestration.read")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancelled_initiator_consent_cannot_be_replayed_to_create_authority(bool netRatelInitiates)
    {
        await using var pair = await ServiceLinkPair.CreateAsync(netRatelInitiates);
        await pair.PrepareAsync(); await pair.ReviewAsync();
        var nrCount = await LocalCountAsync<ServicePrincipalRegistration>(pair);
        var rdCount = await pair.RatelDesk.CountAsync("ServicePrincipalRegistrations");
        using var cancelled = await pair.Initiator.PostAsJsonAsync($"/api/v1/admin/service-links/attempts/{pair.Start.AttemptId}/cancel", new ServiceLinkAdminAction("test-cancel"));
        Assert.True(cancelled.IsSuccessStatusCode);
        using var replay = await pair.Initiator.PostAsJsonAsync($"/api/v1/admin/service-links/attempts/{pair.Start.AttemptId}/approve", new ServiceLinkLocalApproveRequest(pair.Review.GrantHash, pair.SessionBinding));
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(nrCount, await LocalCountAsync<ServicePrincipalRegistration>(pair));
        Assert.Equal(rdCount, await pair.RatelDesk.CountAsync("ServicePrincipalRegistrations"));
        if (netRatelInitiates)
        {
            using var continued = await pair.NetRatel.AdminAsync($"/api/v1/admin/service-links/attempts/{pair.Start.AttemptId}/continue", new ServiceLinkContinueRequest(pair.SessionBinding));
            Assert.Equal(HttpStatusCode.Forbidden, continued.StatusCode);
            Assert.Equal(nrCount, await LocalCountAsync<ServicePrincipalRegistration>(pair));
        }
        var status = await pair.StatusAsync(pair.Initiator);
        Assert.Equal("abort", status.Decision); Assert.False(status.LocalInboundActive); Assert.False(status.LocalBusinessSenderEnabled);
    }

    [Fact]
    public async Task Prepared_NetRatel_responder_retains_in_doubt_decision_and_accepts_authenticated_late_coordinator_commit()
    {
        await using var pair = await ServiceLinkPair.CreateAsync(false);
        await pair.PrepareAsync(); await pair.ReviewAsync(); await pair.ApproveAsync();
        await pair.ResumeAsync(pair.Initiator);
        var prepared = await pair.StatusAsync(pair.NetRatel.Administrator);
        Assert.True(prepared.LocalOutboundPersisted); Assert.False(prepared.LocalBusinessSenderEnabled);
        pair.NetRatel.Clock.Advance(TimeSpan.FromSeconds(125));
        await pair.ResumeAsync(pair.NetRatel.Administrator);
        Assert.Equal("in_doubt", (await pair.StatusAsync(pair.NetRatel.Administrator)).LifecycleState);
        Assert.Equal(1, await LocalCountAsync<ServicePrincipalRegistration>(pair));
        await pair.NetRatel.RestartAsync();
        await pair.FinishAsync();
        Assert.Equal("commit", (await pair.StatusAsync(pair.NetRatel.Administrator)).Decision);
        Assert.Equal(1, await LocalCountAsync<ServicePrincipalRegistration>(pair));
    }

    [Fact]
    public async Task Linked_inbound_revocation_rejects_cached_business_tokens_and_preserves_bound_control_recovery_after_restart()
    {
        await using var pair = await ServiceLinkPair.CreateAsync(true); await pair.ActivateAsync();
        var status = await pair.StatusAsync(pair.NetRatel.Administrator);
        var business = await pair.TokenAsync(true, "netratel.orchestration.read");
        var control = await pair.TokenAsync(true, ServiceLinkContract.ControlScope);
        var principalId = (await LocalIdsAsync<ServicePrincipalRegistration>(pair, p => p.Id)).Single();
        await AssertLinkedPrincipalRequiresUnlinkAsync(pair);
        // Inject independent durable-authority revocation through the registry.
        // Linked human management remains bound to the reciprocal unlink workflow.
        await using (var scope = pair.NetRatel.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>().RevokeAsync(principalId);
        Assert.Equal(HttpStatusCode.Unauthorized, await ServiceLinkPair.GetWithTokenAsync(pair.NetRatel.Anonymous, "/internal/health", business));
        Assert.Equal(HttpStatusCode.OK, await ServiceLinkPair.GetWithTokenAsync(pair.NetRatel.Anonymous, $"{ServiceLinkContract.EndpointPath}/links/{status.LinkId}/status", control));
        var unavailable = await pair.StatusAsync(pair.NetRatel.Administrator);
        Assert.False(unavailable.LocalInboundActive); Assert.False(unavailable.LocalBusinessSenderEnabled); Assert.Equal("grant-unavailable", unavailable.LastErrorCode);
        await pair.NetRatel.RestartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await ServiceLinkPair.GetWithTokenAsync(pair.NetRatel.Anonymous, "/internal/health", business));
        Assert.Equal(HttpStatusCode.OK, await ServiceLinkPair.GetWithTokenAsync(pair.NetRatel.Anonymous, $"{ServiceLinkContract.EndpointPath}/links/{status.LinkId}/status", control));
        Assert.False((await pair.StatusAsync(pair.NetRatel.Administrator)).LocalBusinessSenderEnabled);
        Assert.Equal(principalId, (await LocalIdsAsync<ServicePrincipalRegistration>(pair, p => p.Id)).Single());
        Assert.Equal(1, await pair.RatelDesk.CountAsync("IncidentReceiverSources"));
    }

    [Fact]
    public async Task Invalid_terminal_reason_and_unrelated_lifecycle_fields_do_not_mutate_the_real_operation_journal()
    {
        await using var pair = await ServiceLinkPair.CreateAsync(true); await pair.ActivateAsync();
        var status = await pair.StatusAsync(pair.NetRatel.Administrator);
        var token = await pair.TokenAsync(true, ServiceLinkContract.ControlScope);
        var before = await LocalCountAsync<ServiceLinkOperation>(pair);
        foreach (var reason in new string?[] { null, "", new('a', 257) })
        {
            using var rejected = await pair.NetRatel.ServiceAsync($"{ServiceLinkContract.EndpointPath}/links/{status.LinkId}/revoke", token,
                new ServiceLinkLifecycleRequest { OperationId = Guid.NewGuid().ToString("N"), LinkId = status.LinkId!, LinkRevision = status.LinkRevision,
                    GrantHash = status.GrantHash!, ExpectedLinkRevision = status.LinkRevision, RevocationId = Guid.NewGuid().ToString("N"), ReasonCode = reason });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        var payload = ServiceLinkLifecycleProjection.Build("revoke", new ServiceLinkLifecycleRequest
        {
            OperationId = Guid.NewGuid().ToString("N"), LinkId = status.LinkId!, LinkRevision = status.LinkRevision, GrantHash = status.GrantHash!,
            ExpectedLinkRevision = status.LinkRevision, RevocationId = Guid.NewGuid().ToString("N"), ReasonCode = "test"
        });
        payload["rotation_id"] = null;
        using (var rejected = await pair.NetRatel.ServiceAsync($"{ServiceLinkContract.EndpointPath}/links/{status.LinkId}/revoke", token, payload))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(before, await LocalCountAsync<ServiceLinkOperation>(pair));
        Assert.True((await pair.StatusAsync(pair.NetRatel.Administrator)).LocalBusinessSenderEnabled);
    }

    private static async Task AssertHumanIsolationAsync(ServiceLinkPair pair, string actuallyAcceptedToken)
    {
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(actuallyAcceptedToken).Claims.ToArray();
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
        var before = await db.ApplicationPrincipals.CountAsync();
        var administratorId = await db.Users.Where(u => u.Email == global::ApiFactory.LocalAdministratorEmail).Select(u => u.PrincipalId).SingleAsync();
        var access = scope.ServiceProvider.GetRequiredService<IEffectiveAccessService>();
        var resolver = scope.ServiceProvider.GetRequiredService<IApplicationPrincipalResolver>();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        foreach (var marker in new[] { "token_use", "auth_mode" })
        {
            // Deliberate malicious human/legacy claims cannot undo either independent service marker.
            // This projection test starts from a token already accepted over actual HTTP above.
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims.Where(c => c.Type is not ("token_use" or "auth_mode" or "client_id" or "scope"))
                .Concat(new[] { new Claim(marker, marker == "token_use" ? "netratel_service" : "service"),
                    new Claim("roles", "Operator"), new Claim(ClaimTypes.Role, "Operator"), new Claim("groups", "Operator"),
                    new Claim("netratel_principal_id", administratorId!), new Claim("client_id", "synthetic-legacy-allowed"), new Claim("scope", "netratel.api") }), "ManagedService"));
            var snapshot = await access.GetSnapshotAsync(principal, int.Parse(pair.NetRatel.TenantId));
            Assert.Null(snapshot.PrincipalId); Assert.False(snapshot.IsInstanceAdministrator); Assert.False(snapshot.IsLegacyOperator); Assert.Empty(snapshot.Permissions);
            Assert.False(await access.AuthorizeAsync(principal, NetRatelPermissions.IntegrationManagement, int.Parse(pair.NetRatel.TenantId)));
            Assert.Empty((await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.IntegrationManagement))!);
            Assert.Null(await resolver.ResolveExternalAsync(principal));
            var resource = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            Assert.False((await authorization.AuthorizeAsync(principal, resource, options.DefaultPolicy)).Succeeded);
            foreach (var policy in new[] { "Operator", "M2MOnly", "HealthRead", "ClientArtifactsUpload" })
                Assert.False((await authorization.AuthorizeAsync(principal, resource, policy)).Succeeded, $"The service marker escaped into {policy}.");
        }
        Assert.Equal(before, await db.ApplicationPrincipals.CountAsync());
    }

    private static async Task AssertCatalogBoundAsync(ServiceLinkPair pair, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/catalog/request-definitions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await pair.NetRatel.Anonymous.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var definitions = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(1, definitions.GetArrayLength());
        Assert.Equal(pair.NetRatel.RequestDefinitionId.ToString(System.Globalization.CultureInfo.InvariantCulture), definitions[0].GetProperty("requestDefinitionId").GetString());
    }

    private static async Task AssertActualReceiverReplayAsync(ServiceLinkPair pair)
    {
        var token = await pair.TokenAsync(false, "rateldesk.incidents.create rateldesk.incident-receipts.read");
        var key = "pair-proof-" + Guid.NewGuid().ToString("N");
        object Body(string title) => new { title, description = "Synthetic two-product receipt proof", priority = 0,
            customerId = pair.RatelDesk.CustomerId, organizationId = pair.RatelDesk.OrganizationId };
        async Task<HttpResponseMessage> Create(string title)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-NetRatel-Source-Instance", pair.NetRatel.SourceInstanceId.ToString("D")); request.Headers.Add("Idempotency-Key", key);
            request.Content = JsonContent.Create(Body(title)); return await pair.RatelDesk.Anonymous.SendAsync(request);
        }
        pair.RatelDesk.Proxy.LoseNextCompletedResponse("/api/v1/incidents/");
        await Assert.ThrowsAsync<HttpRequestException>(async () => { using var lost = await Create("Synthetic exactly-once incident"); });
        Assert.Equal(1, pair.RatelDesk.Proxy.LostResponses);
        using (var replay = await Create("Synthetic exactly-once incident")) Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using (var changed = await Create("Changed semantic incident")) Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var receiptRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/integrations/netratel/incident-receipts/" + key);
        receiptRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        receiptRequest.Headers.Add("X-NetRatel-Source-Instance", pair.NetRatel.SourceInstanceId.ToString("D"));
        using var receipt = await pair.RatelDesk.Anonymous.SendAsync(receiptRequest); Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Equal(1, await pair.RatelDesk.CountAsync("Incidents")); Assert.Equal(1, await pair.RatelDesk.CountAsync("IncidentCreateReceipts"));
    }

    private static async Task<int> LocalCountAsync<T>(ServiceLinkPair pair) where T : class
    { await using var scope = pair.NetRatel.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<T>().CountAsync(); }

    private static async Task<Guid[]> LocalIdsAsync<T>(ServiceLinkPair pair, System.Linq.Expressions.Expression<Func<T, Guid>> selector) where T : class
    { await using var scope = pair.NetRatel.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<T>().OrderBy(selector).Select(selector).ToArrayAsync(); }
}

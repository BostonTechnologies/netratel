using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Security.Local;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Connectivity;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class IntegrationCredentialAccessEndpointTests
{
    [Fact]
    public async Task Browser_review_adds_a_new_tenant_and_edits_scopes_without_replacing_secret_expiry_or_policies()
    {
        await using var fixture = await ReviewedMcpAccessTests.Fixture.CreateAsync();
        (await fixture.Identity.Users.SingleAsync(user => user.PrincipalId == "requester")).IsInstanceAdministrator = true;
        var original = await fixture.Identity.IntegrationCredentials.SingleAsync();
        original.ExpiresAtUtc = original.ExpiresAtUtc.AddTicks(1234560);
        var originalExpiry = original.ExpiresAtUtc;
        var originalHash = original.SecretHash;
        await fixture.Identity.SaveChangesAsync();
        fixture.App.Tenants.Add(new Tenant { Id = 41, Name = "New synthetic customer" });
        await fixture.App.SaveChangesAsync();
        await using var app = await BuildAppAsync(fixture);
        using var client = AccountClient(app);
        var path = AccessPath(fixture);
        var proposal = new IntegrationCredentialEndpoints.CredentialAccessRequest(Guid.NewGuid().ToString(),
            [new(17, NetRatelPermissions.McpDiscoveryRead), new(29, NetRatelPermissions.FlowRead), new(41, NetRatelPermissions.FlowRead)],
            [NetRatelPermissions.McpDiscoveryRead, NetRatelPermissions.FlowRead]);

        using var prepared = await client.PostAsJsonAsync(path, proposal);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        Assert.True(prepared.Headers.CacheControl!.NoStore);
        var pending = (await prepared.Content.ReadFromJsonAsync<ReviewedMcpAccessReceipt>())!;
        Assert.Equal("pending", pending.Request.Status);
        Assert.Equal(original.Name, pending.Request.Proposal.Name);
        Assert.Equal(originalExpiry, pending.Request.Proposal.ExpiresAtUtc);
        Assert.Equal(original.Id, pending.Request.Proposal.CredentialId);
        Assert.Empty(pending.Request.Proposal.Targets);
        Assert.Equal(2, (await fixture.Credentials.ListAsync("requester")).Single().Grants.Count);
        Assert.DoesNotContain(fixture.Credential.Secret, await prepared.Content.ReadAsStringAsync());
        Assert.DoesNotContain("DeviceCapability", await prepared.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var applyPath = path + "/" + pending.Request.RequestId + "/apply";
        using var changedDecision = await client.PostAsJsonAsync(applyPath, new { proposalHash = "changed", confirm = true });
        Assert.Equal(HttpStatusCode.BadRequest, changedDecision.StatusCode);
        var decision = new IntegrationCredentialEndpoints.CredentialAccessDecision(pending.Request.ProposalHash, true);
        using var applied = await client.PostAsJsonAsync(applyPath, decision);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var receipt = (await applied.Content.ReadFromJsonAsync<ReviewedMcpAccessReceipt>())!;
        Assert.Equal("consumed", receipt.Request.Status);
        Assert.Equal("owner_direct", receipt.Request.ApprovalMode);
        Assert.Equal(proposal.Grants, receipt.ConfiguredGrants);
        Assert.Equal(proposal.InstancePermissions.Order(), receipt.ConfiguredInstancePermissions.Order());
        var saved = (await fixture.Credentials.ListAsync("requester")).Single();
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal(original.Name, saved.Name);
        Assert.Equal(originalExpiry, saved.ExpiresAtUtc);
        Assert.Equal(IntegrationCredentialPurpose.HttpMcp, saved.Purpose);
        Assert.Equal(ReviewedMcpAccessTests.Fixture.Resource, saved.Resource);
        Assert.Equal(originalHash, (await fixture.Identity.IntegrationCredentials.SingleAsync()).SecretHash);
        Assert.NotNull(await fixture.Credentials.VerifyAsync(fixture.Credential.Secret, IntegrationCredentialPurpose.HttpMcp));
        Assert.Empty(await fixture.App.McpOperatorPolicies.ToArrayAsync());
        using var replay = await client.PostAsJsonAsync(applyPath, decision);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Single(await fixture.Identity.IntegrationCredentialLifecycleEvents.Where(item => item.Action == "reconciled").ToArrayAsync());

        var reduced = proposal with { RequestId = Guid.NewGuid().ToString(),
            Grants = [new(29, NetRatelPermissions.FlowRead)], InstancePermissions = [NetRatelPermissions.McpDiscoveryRead] };
        using var reduceRequest = await client.PostAsJsonAsync(path, reduced);
        var reduceReceipt = (await reduceRequest.Content.ReadFromJsonAsync<ReviewedMcpAccessReceipt>())!;
        using var reduceApply = await client.PostAsJsonAsync(path + "/" + reduceReceipt.Request.RequestId + "/apply",
            new IntegrationCredentialEndpoints.CredentialAccessDecision(reduceReceipt.Request.ProposalHash, true));
        Assert.Equal(HttpStatusCode.OK, reduceApply.StatusCode);
        var remaining = (await fixture.Credentials.ListAsync("requester")).Single();
        Assert.Equal([new IntegrationCredentialGrantRequest(29, NetRatelPermissions.FlowRead)], remaining.Grants);
        Assert.Equal([NetRatelPermissions.McpDiscoveryRead], remaining.InstancePermissions);
        Assert.Equal(originalExpiry, remaining.ExpiresAtUtc);
    }

    [Fact]
    public async Task Pending_browser_request_can_be_recovered_and_applied_after_independent_review()
    {
        await using var fixture = await ReviewedMcpAccessTests.Fixture.CreateAsync();
        await using var app = await BuildAppAsync(fixture);
        using var client = AccountClient(app);
        var path = AccessPath(fixture);
        using var prepared = await client.PostAsJsonAsync(path, Request());
        var pending = (await prepared.Content.ReadFromJsonAsync<ReviewedMcpAccessReceipt>())!;
        var decision = new IntegrationCredentialEndpoints.CredentialAccessDecision(pending.Request.ProposalHash, true);
        using var cannotSelfApprove = await client.PostAsJsonAsync(path + "/" + pending.Request.RequestId + "/apply", decision);
        Assert.Equal(HttpStatusCode.Forbidden, cannotSelfApprove.StatusCode);
        Assert.Equal("review_approver_authority_missing", (await cannotSelfApprove.Content.ReadFromJsonAsync<IntegrationPairingFailure>())!.Code);
        var requests = await client.GetFromJsonAsync<ReviewedMcpAccessRequest[]>(path);
        Assert.Equal(pending.Request.RequestId, Assert.Single(requests!).RequestId);
        await fixture.Pairing.ApproveAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", "reviewer")], "review-test")), fixture.Approval(pending));
        var approved = (await client.GetFromJsonAsync<ReviewedMcpAccessReceipt>(path + "/" + pending.Request.RequestId))!;
        Assert.Equal("approved", approved.Request.Status);
        Assert.Equal(2, (await fixture.Credentials.ListAsync("requester")).Single().Grants.Count);
        using var applied = await client.PostAsJsonAsync(path + "/" + pending.Request.RequestId + "/apply", decision);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal("consumed", (await applied.Content.ReadFromJsonAsync<ReviewedMcpAccessReceipt>())!.Request.Status);
        Assert.Single((await fixture.Credentials.ListAsync("requester")).Single().Grants);
        Assert.Empty(await fixture.App.McpOperatorPolicies.ToArrayAsync());
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.Unauthorized)]
    [InlineData("integration_credential", "1", HttpStatusCode.Forbidden)]
    [InlineData("local", null, HttpStatusCode.Forbidden)]
    [InlineData("local", "invalid", HttpStatusCode.Forbidden)]
    public async Task Grant_editor_rejects_noninteractive_and_unvalidated_cookie_mutations(string? identity, string? mutation, HttpStatusCode expected)
    {
        await using var fixture = await ReviewedMcpAccessTests.Fixture.CreateAsync();
        await using var app = await BuildAppAsync(fixture);
        using var client = app.GetTestClient();
        if (identity is not null) client.DefaultRequestHeaders.Add("X-Test-Identity", identity);
        if (mutation is not null) client.DefaultRequestHeaders.Add("X-NetRatel-Account-Request", mutation);
        using var response = await client.PostAsJsonAsync(AccessPath(fixture), Request());
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(await fixture.Identity.IntegrationPairingRequests.ToArrayAsync());
    }

    [Fact]
    public async Task Unowned_unassignable_changed_and_policy_bearing_requests_cannot_apply_through_grant_editor()
    {
        await using var fixture = await ReviewedMcpAccessTests.Fixture.CreateAsync();
        await using var app = await BuildAppAsync(fixture);
        using var client = AccountClient(app);
        var path = AccessPath(fixture);
        var request = Request();
        client.DefaultRequestHeaders.Remove("X-Test-Owner");
        client.DefaultRequestHeaders.Add("X-Test-Owner", "reviewer");
        using var unowned = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.BadRequest, unowned.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Owner");
        client.DefaultRequestHeaders.Add("X-Test-Owner", "requester");
        using var unassignable = await client.PostAsJsonAsync(path, request with { Grants = [new(17, NetRatelPermissions.TelemetryRead)] });
        Assert.Equal(HttpStatusCode.Forbidden, unassignable.StatusCode);
        Assert.Empty(await fixture.Identity.IntegrationPairingRequests.ToArrayAsync());
        using var prepared = await client.PostAsJsonAsync(path, request);
        var pending = (await prepared.Content.ReadFromJsonAsync<ReviewedMcpAccessReceipt>())!;
        var credential = await fixture.Identity.IntegrationCredentials.Include(item => item.Grants).SingleAsync();
        var removed = credential.Grants.Single(grant => grant.TenantId == 29);
        fixture.Identity.Remove(removed);
        credential.Grants.Remove(removed);
        (await fixture.Identity.Users.SingleAsync(user => user.PrincipalId == "requester")).IsInstanceAdministrator = true;
        await fixture.Identity.SaveChangesAsync();
        using var changed = await client.PostAsJsonAsync(path + "/" + pending.Request.RequestId + "/apply",
            new IntegrationCredentialEndpoints.CredentialAccessDecision(pending.Request.ProposalHash, true));
        Assert.Equal(HttpStatusCode.Forbidden, changed.StatusCode);
        Assert.Equal("review_credential_changed", (await changed.Content.ReadFromJsonAsync<IntegrationPairingFailure>())!.Code);

        var policyProposal = fixture.Proposal("reconcile", fixture.Credential.CredentialId) with
        { Name = credential.Name, ExpiresAtUtc = credential.ExpiresAtUtc };
        var policyRequest = await fixture.Pairing.RequestReviewedAsync(policyProposal,
            "requester", fixture.Credential.CredentialId, ReviewedMcpAccessTests.Fixture.Origin, ReviewedMcpAccessTests.Fixture.Resource,
            NetRatel.Application.Operations.McpOperatorEnvironment.Production);
        using var policyApply = await client.PostAsJsonAsync(path + "/" + policyRequest.Request.RequestId + "/apply",
            new IntegrationCredentialEndpoints.CredentialAccessDecision(policyRequest.Request.ProposalHash, true));
        Assert.Equal(HttpStatusCode.BadRequest, policyApply.StatusCode);
        Assert.Empty(await fixture.App.McpOperatorPolicies.ToArrayAsync());
        Assert.Empty(await fixture.Identity.IntegrationCredentialLifecycleEvents.Where(item => item.Action == "reconciled").ToArrayAsync());
    }

    private static IntegrationCredentialEndpoints.CredentialAccessRequest Request() => new(Guid.NewGuid().ToString(),
        [new(17, NetRatelPermissions.FlowRead)], [NetRatelPermissions.McpDiscoveryRead]);
    private static string AccessPath(ReviewedMcpAccessTests.Fixture fixture) =>
        $"/api/v2/account/integration-credentials/{fixture.Credential.CredentialId}/access-requests";
    private static HttpClient AccountClient(WebApplication app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "local");
        client.DefaultRequestHeaders.Add("X-Test-Owner", "requester");
        client.DefaultRequestHeaders.Add("X-NetRatel-Account-Request", "1");
        return client;
    }

    private static async Task<WebApplication> BuildAppAsync(ReviewedMcpAccessTests.Fixture fixture)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration["Integration:HttpMcp:PublicResourceUri"] = ReviewedMcpAccessTests.Fixture.Resource;
        builder.Services.Configure<DeploymentBrandingOptions>(options => options.SiteUrl = ReviewedMcpAccessTests.Fixture.Origin);
        builder.Services.AddAuthentication("AccessTest").AddScheme<AuthenticationSchemeOptions, AccountAuthentication>("AccessTest", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("InteractiveAccount", policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context => context.User.Identities.Any(identity => identity.IsAuthenticated &&
                (identity.AuthenticationType == "Oidc" || identity.AuthenticationType == LocalAuthenticationOptions.Scheme)));
        }));
        builder.Services.AddSingleton(fixture.Identity);
        builder.Services.AddSingleton(fixture.App);
        builder.Services.AddSingleton(fixture.EffectiveAccess);
        builder.Services.AddSingleton<IIntegrationCredentialService>(fixture.Credentials);
        builder.Services.AddSingleton(fixture.Pairing);
        builder.Services.AddSingleton<TimeProvider>(fixture.Clock);
        builder.Services.AddScoped<IIntegrationCredentialGrantValidator, IntegrationCredentialGrantValidator>();
        builder.Services.AddScoped<IDeploymentBrandingService, DeploymentBrandingService>();
        builder.Services.AddRateLimiter(options => options.AddPolicy("integration-pairing-account", _ => RateLimitPartition.GetNoLimiter("test")));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapIntegrationCredentialEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class AccountAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var mode = Request.Headers["X-Test-Identity"].ToString();
            if (mode.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("netratel_principal_id", Request.Headers["X-Test-Owner"].FirstOrDefault() ?? "requester"), new Claim("auth_mode", mode)],
                mode == "local" ? LocalAuthenticationOptions.Scheme : mode);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "AccessTest")));
        }
    }
}

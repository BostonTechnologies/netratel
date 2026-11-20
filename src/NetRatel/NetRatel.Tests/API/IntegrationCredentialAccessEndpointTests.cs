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
using NetRatel.Application.Operations;
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
    public async Task Account_grant_validation_reports_safe_counts_and_does_not_prepare_invalid_reviews()
    {
        await using var fixture = await ReviewedMcpAccessTests.Fixture.CreateAsync();
        await using var app = await BuildAppAsync(fixture);
        using var client = AccountClient(app);
        var path = AccessPath(fixture);
        var limits = IntegrationPermissionCatalog.Limits;
        var cases = new (IntegrationPairingGrant[] Grants, string Code, int? Count, int? Limit)[]
        {
            (Enumerable.Range(1, limits.TenantScopes + 1).Select(id => new IntegrationPairingGrant(id, NetRatelPermissions.FlowRead)).ToArray(),
                "integration_tenant_capacity", limits.TenantScopes + 1, limits.TenantScopes),
            (Enumerable.Repeat(new IntegrationPairingGrant(17, NetRatelPermissions.FlowRead), limits.TenantGrants + 1).ToArray(),
                "integration_grant_capacity", limits.TenantGrants + 1, limits.TenantGrants),
            (Enumerable.Repeat(new IntegrationPairingGrant(17, NetRatelPermissions.FlowRead), limits.PermissionsPerScope + 1).ToArray(),
                "integration_scope_capacity", limits.PermissionsPerScope + 1, limits.PermissionsPerScope),
            ([new(17, NetRatelPermissions.FlowRead), new(17, NetRatelPermissions.FlowRead)], "integration_grants_invalid", null, null),
            ([new(17, "unknown.permission")], "integration_grants_invalid", null, null),
            ([null!], "integration_grants_invalid", null, null),
            ([new(391, NetRatelPermissions.FlowRead)], "integration_tenant_unavailable", null, null)
        };
        foreach (var (grants, code, count, limit) in cases)
        {
            using var response = await client.PostAsJsonAsync(path, Request() with { Grants = grants });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var failure = Assert.IsType<IntegrationPairingFailure>(await response.Content.ReadFromJsonAsync<IntegrationPairingFailure>());
            Assert.Equal(code, failure.Code);
            Assert.Equal("request", failure.Stage);
            Assert.Equal("grants", failure.Field);
            Assert.Equal(count, failure.RequestedCount);
            Assert.Equal(limit, failure.Limit);
            Assert.Equal(Assert.Single(response.Headers.GetValues("X-Correlation-ID")), failure.CorrelationId);
        }
        Assert.Empty(await fixture.Identity.IntegrationPairingRequests.ToArrayAsync());
    }

    [Theory]
    [InlineData("local")]
    [InlineData("Oidc")]
    public async Task Browser_review_adds_a_new_tenant_and_edits_scopes_without_replacing_secret_expiry_and_reconciles_reviewed_base_policies(string identity)
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
        using var client = AccountClient(app, identity);
        var path = AccessPath(fixture);
        var proposal = new IntegrationCredentialEndpoints.CredentialAccessRequest(Guid.NewGuid().ToString(),
            [new(17, NetRatelPermissions.TelemetryRead), new(29, NetRatelPermissions.FlowRead), new(41, NetRatelPermissions.FlowRead)],
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
        Assert.Equal(4, await fixture.App.McpOperatorPolicies.CountAsync(p => p.ParentPolicyId == null && p.IntegrationCredentialId == original.Id));
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
    public async Task Reviewed_edit_can_keep_existing_sparse_scopes_above_the_new_tenant_limit()
    {
        await using var fixture = await ReviewedMcpAccessTests.Fixture.CreateAsync();
        (await fixture.Identity.Users.SingleAsync(user => user.PrincipalId == "requester")).IsInstanceAdministrator = true;
        var current = await fixture.Identity.IntegrationCredentials.Include(credential => credential.Grants).SingleAsync();
        var oldTenantIds = Enumerable.Range(101, IntegrationCredentialLimits.MaximumTenantScopes - 1).ToArray();
        fixture.App.Tenants.AddRange(oldTenantIds.Append(301).Select(id => new Tenant { Id = id, Name = $"Customer {id}" }));
        current.Grants.AddRange(oldTenantIds.Select(id => new IntegrationCredentialGrant
        { CredentialId = current.Id, TenantId = id, Permission = NetRatelPermissions.FlowRead }));
        await fixture.App.SaveChangesAsync();
        await fixture.Identity.SaveChangesAsync();
        await using var app = await BuildAppAsync(fixture);
        using var client = AccountClient(app);
        var path = AccessPath(fixture);
        var grants = current.Grants.Select(grant => new IntegrationPairingGrant(grant.TenantId, grant.Permission)).ToArray();
        var review = Request() with { Grants = grants };
        using var rejected = await client.PostAsJsonAsync(path, review with
        {
            Grants = grants.Where(grant => grant.TenantId != 17).Append(new(301, NetRatelPermissions.FlowRead)).ToArray()
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("integration_tenant_capacity", (await rejected.Content.ReadFromJsonAsync<IntegrationPairingFailure>())!.Code);
        using var prepared = await client.PostAsJsonAsync(path, review);
        prepared.EnsureSuccessStatusCode();
        var pending = (await prepared.Content.ReadFromJsonAsync<ReviewedMcpAccessReceipt>())!;
        using var applied = await client.PostAsJsonAsync(path + $"/{pending.Request.RequestId}/apply",
            new IntegrationCredentialEndpoints.CredentialAccessDecision(pending.Request.ProposalHash, true));
        applied.EnsureSuccessStatusCode();
        Assert.Equal(grants.Length, (await applied.Content.ReadFromJsonAsync<ReviewedMcpAccessReceipt>())!.ConfiguredGrants.Count);
    }

    [Fact]
    public async Task Tenant_deleted_after_review_returns_a_grant_error_without_applying()
    {
        await using var fixture = await ReviewedMcpAccessTests.Fixture.CreateAsync();
        (await fixture.Identity.Users.SingleAsync(user => user.PrincipalId == "requester")).IsInstanceAdministrator = true;
        await fixture.Identity.SaveChangesAsync();
        await using var app = await BuildAppAsync(fixture);
        using var client = AccountClient(app);
        var path = AccessPath(fixture);
        using var prepared = await client.PostAsJsonAsync(path, Request());
        prepared.EnsureSuccessStatusCode();
        var pending = (await prepared.Content.ReadFromJsonAsync<ReviewedMcpAccessReceipt>())!;
        fixture.App.Tenants.Remove(await fixture.App.Tenants.SingleAsync(tenant => tenant.Id == 17));
        await fixture.App.SaveChangesAsync();
        using var applied = await client.PostAsJsonAsync(path + $"/{pending.Request.RequestId}/apply",
            new IntegrationCredentialEndpoints.CredentialAccessDecision(pending.Request.ProposalHash, true));
        Assert.Equal(HttpStatusCode.BadRequest, applied.StatusCode);
        var failure = (await applied.Content.ReadFromJsonAsync<IntegrationPairingFailure>())!;
        Assert.Equal("integration_tenant_unavailable", failure.Code);
        Assert.Equal("grants", failure.Field);
        Assert.Empty(await fixture.App.McpOperatorPolicies.ToArrayAsync());
        Assert.Equal(2, (await fixture.Credentials.ListAsync("requester")).Single().Grants.Count);
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
        Assert.Equal(2, await fixture.App.McpOperatorPolicies.CountAsync(p => p.ParentPolicyId == null && p.IntegrationCredentialId == fixture.Credential.CredentialId));
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
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
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
    [Theory]
    [InlineData("local")]
    [InlineData("Oidc")]
    public async Task Literal_page_binding_returns_invalid_id_with_a_safe_correlated_response(string identity)
    {
        await using var fixture = await ReviewedMcpAccessTests.Fixture.CreateAsync();
        await using var app = await BuildAppAsync(fixture);
        using var client = AccountClient(app, identity);
        using var invalid = await client.GetAsync("/api/v2/account/integration-credentials/detail.Id/access-requests");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var failure = (await invalid.Content.ReadFromJsonAsync<IntegrationPairingFailure>())!;
        Assert.Equal("review_credential_id_invalid", failure.Code);
        Assert.Equal("list", failure.Stage);
        Assert.False(string.IsNullOrWhiteSpace(failure.CorrelationId));
        Assert.Equal(failure.CorrelationId, Assert.Single(invalid.Headers.GetValues("X-Correlation-ID")));
        Assert.DoesNotContain(fixture.Credential.Secret, await invalid.Content.ReadAsStringAsync());
        using var valid = await client.GetAsync(AccessPath(fixture));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Empty((await valid.Content.ReadFromJsonAsync<ReviewedMcpAccessRequest[]>())!);
    }

    internal static HttpClient AccountClient(WebApplication app, string identity = "local", string owner = "requester")
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", identity);
        client.DefaultRequestHeaders.Add("X-Test-Owner", owner);
        client.DefaultRequestHeaders.Add("X-NetRatel-Account-Request", "1");
        return client;
    }

    private static async Task<WebApplication> BuildAppAsync(ReviewedMcpAccessTests.Fixture fixture)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration["Integration:HttpMcp:PublicResourceUri"] = ReviewedMcpAccessTests.Fixture.Resource;
        builder.Services.Configure<DeploymentBrandingOptions>(options => options.SiteUrl = ReviewedMcpAccessTests.Fixture.Origin);
        AddAccountAuthentication(builder.Services);
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

    internal static void AddAccountAuthentication(IServiceCollection services)
    {
        services.AddAuthentication("AccessTest").AddScheme<AuthenticationSchemeOptions, AccountAuthentication>("AccessTest", _ => { });
        services.AddAuthorization(options => options.AddPolicy("InteractiveAccount", policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context => context.User.Identities.Any(identity => identity.IsAuthenticated &&
                (identity.AuthenticationType == "Oidc" || identity.AuthenticationType == LocalAuthenticationOptions.Scheme)));
        }));
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

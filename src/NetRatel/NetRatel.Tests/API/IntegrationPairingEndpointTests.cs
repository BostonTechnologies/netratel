using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Security.Integration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Connectivity;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class IntegrationPairingEndpointTests
{
    [Fact]
    public async Task Host_generation_requires_current_instance_administrator_and_never_issues_a_personal_or_outbound_secret()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();
        var capabilities = (await client.GetFromJsonAsync<HttpHostSetupCapabilities>(HttpHostSetupProtocol.Route + "/capabilities"))!;
        using var wrongOrigin = await client.PostAsJsonAsync(HttpHostSetupProtocol.Route + "/start",
            new HttpHostSetupStartRequest("https://attacker.example.test", capabilities.Resource, new string('A', 64)));
        wrongOrigin.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var start = await client.PostAsJsonAsync(HttpHostSetupProtocol.Route + "/start",
            new HttpHostSetupStartRequest("https://netratel.example.test:443", capabilities.Resource, new string('a', 64)));
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        start.Headers.CacheControl!.NoStore.Should().BeTrue();
        var request = (await start.Content.ReadFromJsonAsync<HttpHostSetupStartResponse>())!;
        request.VerificationUri.Should().NotContain(request.UserCode).And.NotContain(request.DeviceCapability);
        var decision = new HttpHostSetupCodeRequest(request.UserCode);
        (await client.PostAsJsonAsync(HttpHostSetupProtocol.AccountRoute + "/approve", decision)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add("X-Test-Account", "owner");
        (await client.PostAsJsonAsync(HttpHostSetupProtocol.AccountRoute + "/approve", decision)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Add("X-NetRatel-Account-Request", "1");
        (await client.PostAsJsonAsync(HttpHostSetupProtocol.AccountRoute + "/lookup", decision)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            db.ApplicationPrincipals.Add(new ApplicationPrincipal { Id = "owner", LocalUserId = "host-owner" });
            db.Users.Add(new LocalUser { Id = "host-owner", PrincipalId = "owner", IsEnabled = true, IsInstanceAdministrator = true });
            await db.SaveChangesAsync();
        }
        var reviewResponse = await client.PostAsJsonAsync(HttpHostSetupProtocol.AccountRoute + "/lookup", decision);
        reviewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var review = (await reviewResponse.Content.ReadFromJsonAsync<HttpHostSetupReview>())!;
        review.RegistrationFingerprint.Should().Be(new string('A', 64));
        review.ServicePrincipal.Should().Be("netratel-mcp-http");
        (await client.GetAsync(HttpHostSetupProtocol.AccountRoute + "/approve")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await client.PostAsJsonAsync(HttpHostSetupProtocol.AccountRoute + "/approve", decision)).StatusCode.Should().Be(HttpStatusCode.OK);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            (await db.HttpHostSetupRequests.SingleAsync()).NextPollAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        var pollResponse = await client.PostAsJsonAsync(HttpHostSetupProtocol.Route + "/poll", new HttpHostSetupDeviceRequest(request.DeviceCapability));
        var poll = (await pollResponse.Content.ReadFromJsonAsync<HttpHostSetupPollResponse>())!;
        poll.Status.Should().Be("approved");
        poll.ApprovalId.Should().Be(request.RequestId);
        poll.RegistrationFingerprint.Should().Be(review.RegistrationFingerprint);
        (await pollResponse.Content.ReadAsStringAsync()).Should().NotContain("nrt_ic_").And.NotContain("secret").And.NotContain("owner");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            (await db.IntegrationCredentials.CountAsync()).Should().Be(0);
            (await db.Users.SingleAsync()).IsInstanceAdministrator = false;
            await db.SaveChangesAsync();
        }
        var revokedResponse = await client.PostAsJsonAsync(HttpHostSetupProtocol.Route + "/poll", new HttpHostSetupDeviceRequest(request.DeviceCapability));
        var revoked = (await revokedResponse.Content.ReadFromJsonAsync<HttpHostSetupPollResponse>())!;
        revoked.Status.Should().Be("denied");
        revoked.ApprovalId.Should().BeNull();
        revoked.RegistrationFingerprint.Should().BeNull();
    }

    [Fact]
    public async Task Host_discovery_preserves_explicit_OIDC_ingress_and_marks_unknown_existing_ingress_for_review()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();
        app.Configuration["NetRatel:Mcp:Delegation:Enabled"] = "true";
        app.Configuration["Integration:HttpMcp:AuthenticationMode"] = "";
        app.Configuration["WebGateway:Mcp:AuthenticationMode"] = " ";
        var legacy = (await client.GetFromJsonAsync<HttpHostSetupCapabilities>(HttpHostSetupProtocol.Route + "/capabilities"))!;
        legacy.AuthenticationModeKnown.Should().BeFalse();
        app.Configuration["WebGateway:Mcp:AuthenticationMode"] = "Oidc";
        var gatewayConfigured = (await client.GetFromJsonAsync<HttpHostSetupCapabilities>(HttpHostSetupProtocol.Route + "/capabilities"))!;
        gatewayConfigured.AuthenticationModeKnown.Should().BeTrue();
        gatewayConfigured.LocalCredentialMode.Should().BeFalse();
        app.Configuration["Integration:HttpMcp:AuthenticationMode"] = "invalid";
        (await client.GetAsync(HttpHostSetupProtocol.Route + "/capabilities")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        app.Configuration["Integration:HttpMcp:AuthenticationMode"] = "Oidc";
        app.Configuration["Integration:HttpMcp:Authority"] = "https://identity.example.test/realms/operators";
        app.Configuration["Integration:HttpMcp:RequiredGroups:0"] = "mcp-operators";
        app.Configuration["Integration:HttpMcp:RequiredGroups:1"] = "";
        app.Configuration["Integration:HttpMcp:RequiredScopes:0"] = "mcp.read";
        var capabilities = (await client.GetFromJsonAsync<HttpHostSetupCapabilities>(HttpHostSetupProtocol.Route + "/capabilities"))!;
        capabilities.AuthenticationModeKnown.Should().BeTrue();
        capabilities.LocalCredentialMode.Should().BeFalse();
        capabilities.OidcAuthority.Should().Be("https://identity.example.test/realms/operators");
        capabilities.RequiredGroups.Should().Equal("mcp-operators");
        capabilities.RequiredScopes.Should().Equal("mcp.read");
        var start = await client.PostAsJsonAsync(HttpHostSetupProtocol.Route + "/start",
            new HttpHostSetupStartRequest(capabilities.PublicOrigin, capabilities.Resource, new string('B', 64)));
        var request = (await start.Content.ReadFromJsonAsync<HttpHostSetupStartResponse>())!;
        app.Configuration["Integration:HttpMcp:PublicResourceUri"] = "https://mcp.example.test/changed/mcp";
        var poll = await client.PostAsJsonAsync(HttpHostSetupProtocol.Route + "/poll", new HttpHostSetupDeviceRequest(request.DeviceCapability));
        (await poll.Content.ReadFromJsonAsync<HttpHostSetupPollResponse>())!.Status.Should().Be("cancelled");
    }

    [Fact]
    public async Task Anonymous_capabilities_and_start_use_deployment_origin_and_do_not_enumerate_private_tenants()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Host = "attacker.example.test";
        var capabilities = await client.GetFromJsonAsync<IntegrationPairingCapabilities>(IntegrationPairingProtocol.Route + "/capabilities");
        capabilities!.PublicOrigin.Should().Be("https://netratel.example.test");
        capabilities.HttpMcpResource.Should().Be("https://mcp.example.test/mcp");
        using var rejected = await client.PostAsJsonAsync(IntegrationPairingProtocol.Route + "/start",
            new IntegrationPairingStartRequest("HTTP client", PairingCredentialPurpose.HttpMcp, "https://attacker.example.test/mcp"));
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var response = await client.PostAsJsonAsync(IntegrationPairingProtocol.Route + "/start", new IntegrationPairingStartRequest("CLI client", PairingCredentialPurpose.Api));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var started = (await response.Content.ReadFromJsonAsync<IntegrationPairingStartResponse>())!;
        started.PublicOrigin.Should().Be(capabilities.PublicOrigin);
        started.VerificationUri.Should().NotContain(started.DeviceCapability).And.NotContain(started.UserCode);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Tenant private").And.NotContain("nrt_ic_");
    }

    [Fact]
    public async Task Browser_decisions_require_interactive_account_and_cookie_mutation_validation_and_get_cannot_approve()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();
        var request = new IntegrationPairingApprovalRequest("invalid", DateTimeOffset.UtcNow.AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)]);
        (await client.PostAsJsonAsync(IntegrationPairingProtocol.AccountRoute + "/approve", request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add("X-Test-Account", "owner");
        (await client.GetAsync(IntegrationPairingProtocol.AccountRoute + "/approve")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await client.PostAsJsonAsync(IntegrationPairingProtocol.AccountRoute + "/approve", request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync(IntegrationPairingProtocol.AccountRoute + "/deny", new IntegrationPairingCodeRequest("invalid"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Add("X-NetRatel-Account-Request", "1");
        (await client.PostAsJsonAsync(IntegrationPairingProtocol.AccountRoute + "/approve", request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var scope = app.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>().IntegrationCredentials.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Api_self_status_validates_current_grants_and_rejects_mcp_purpose_revocation_and_membership_loss()
    {
        await using var app = await BuildAppAsync();
        string secret;
        string mcpSecret;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            db.Users.Add(new LocalUser { Id = "status-owner", PrincipalId = "owner", UserName = "status", IsEnabled = true, IsInstanceAdministrator = true });
            await db.SaveChangesAsync();
            var credentials = scope.ServiceProvider.GetRequiredService<IIntegrationCredentialService>();
            secret = (await credentials.CreateAsync("owner", new("Status client", IntegrationCredentialPurpose.Api,
                DateTimeOffset.UtcNow.AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)]))).Secret;
            mcpSecret = (await credentials.CreateAsync("owner", new("MCP client", IntegrationCredentialPurpose.HttpMcp,
                DateTimeOffset.UtcNow.AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)], "https://mcp.example.test/mcp"))).Secret;
        }
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        using var accepted = await client.GetAsync("/api/v1/auth/ai-agent/status");
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await accepted.Content.ReadAsStringAsync();
        body.Should().Contain("\"authenticated\":true").And.Contain("integration_credential").And.NotContain(secret).And.NotContain("isInstanceAdministrator");
        client.DefaultRequestHeaders.Authorization = new("Bearer", mcpSecret);
        (await client.GetAsync("/api/v1/auth/ai-agent/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            (await db.Users.SingleAsync()).IsInstanceAdministrator = false;
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        (await client.GetAsync("/api/v1/auth/ai-agent/status")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var credentials = scope.ServiceProvider.GetRequiredService<IIntegrationCredentialService>();
            var id = (await credentials.ListAsync("owner")).Single(credential => credential.Purpose == IntegrationCredentialPurpose.Api).Id;
            await credentials.RevokeAsync("owner", id, "owner");
        }
        (await client.GetAsync("/api/v1/auth/ai-agent/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<WebApplication> BuildAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Integration:HttpMcp:PublicResourceUri"] = "https://mcp.example.test/mcp";
        builder.Services.Configure<DeploymentBrandingOptions>(options => options.SiteUrl = "https://netratel.example.test");
        builder.Services.AddAuthentication("PairingTest").AddScheme<AuthenticationSchemeOptions, AccountAuthentication>("PairingTest", _ => { })
            .AddScheme<AuthenticationSchemeOptions, IntegrationCredentialAuthenticationHandler>(IntegrationCredentialAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("InteractiveAccount", policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(AiAgentSelfStatusEndpoints.Policy, policy =>
            {
                policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.SchemeName);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(AiAgentSelfStatusEndpoints.HasCurrentIntegrationGrantAsync);
            });
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        var identityName = "pairing-api-" + Guid.NewGuid().ToString("N");
        var applicationName = "pairing-application-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<NetRatelIdentityDbContext>(options => options.UseInMemoryDatabase(identityName));
        builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(applicationName));
        builder.Services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
        builder.Services.AddScoped<IIntegrationCredentialService, IntegrationCredentialService>();
        builder.Services.AddScoped<IIntegrationCredentialCurrentVerifier, IntegrationCredentialService>();
        builder.Services.AddScoped<IIntegrationCredentialGrantValidator, IntegrationCredentialGrantValidator>();
        builder.Services.AddScoped<IDeploymentBrandingService, DeploymentBrandingService>();
        builder.Services.AddScoped<IntegrationPairingService>();
        builder.Services.AddScoped<HttpHostSetupService>();
        builder.Services.AddRateLimiter(options =>
        {
            foreach (var policy in new[] { "integration-pairing-start", "integration-pairing-poll", "integration-pairing-account" })
                options.AddPolicy(policy, _ => RateLimitPartition.GetNoLimiter("test"));
        });
        var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
        app.MapIntegrationPairingEndpoints();
        app.MapAiAgentSelfStatusEndpoints();
        await app.StartAsync();
        return app;
    }
    private sealed class AccountAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.ContainsKey("X-Test-Account") ? AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", "owner"), new Claim("auth_mode", "local")], "PairingTest")), "PairingTest"))
            : AuthenticateResult.NoResult());
    }
}

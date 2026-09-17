using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorAccessEndpointTests
{
    [Fact]
    public void Routes_AreVersionedAndRequireTheMcpM2MIdentity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IMcpOperatorAccessEvaluation>(new RecordingAccessEvaluation());
        var app = builder.Build();

        app.MapMcpOperatorAccessEndpoints();

        var route = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/api/v2/mcp/operator/access/agents/{tenantId:int}/{agentId:guid}/evaluate");
        route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(metadata => metadata.Policy)
            .Should().Contain("M2MOnly");

        app.MapMcpOperatorAuthenticationStatusEndpoints();

        var authenticationStatusRoute = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/api/v2/mcp/operator/auth/status");
        authenticationStatusRoute.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(metadata => metadata.Policy)
            .Should().Contain("M2MOnly");
    }

    [Fact]
    public async Task Evaluate_RequiresExactDelegationAndHidesTargetBeforeTenantVisibility()
    {
        var agentId = Guid.NewGuid();
        var access = new RecordingAccessEvaluation(agentId);
        using var app = await BuildAppAsync(access);
        var client = M2mClient(app);
        var path = $"/api/v2/mcp/operator/access/agents/42/{agentId:D}/evaluate?tool=netratel_terminal&operation=availability";

        var missing = await client.GetAsync(path);

        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("delegated_identity_required");

        var tokens = app.Services.GetRequiredService<McpOperatorDelegationTokenService>();
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, tokens.Create(
            new McpOperatorDelegationIdentity("operator@example.test", "automation-client", "automation-client", [], ["Operator"], ["netratel.mcp.read", "netratel.mcp.observe"]),
            new McpOperatorDelegationRequest("netratel_access", "evaluate", "request-42", "https://mcp.dev.example/mcp", "dev", 42, agentId, "correlation-42")));

        var allowed = await client.GetAsync(path);

        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await allowed.Content.ReadAsStringAsync()).Should().Contain("netratel_terminal").And.Contain("not_checked");
        access.Evaluations.Should().ContainSingle().Which.Should().Be(("netratel_terminal", "availability"));

        using var hiddenApp = await BuildAppAsync(new RecordingAccessEvaluation(agentId, tenantVisible: false));
        var hiddenClient = M2mClient(hiddenApp);
        hiddenClient.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, tokens.Create(
            new McpOperatorDelegationIdentity("operator@example.test", "automation-client", "automation-client", [], ["Operator"], ["netratel.mcp.read"]),
            new McpOperatorDelegationRequest("netratel_access", "target", "request-hidden", "https://mcp.dev.example/mcp", "dev", 42, agentId, "correlation-hidden")));

        var hidden = await hiddenClient.GetAsync($"/api/v2/mcp/operator/access/agents/42/{agentId:D}");
        var hiddenBody = await hidden.Content.ReadAsStringAsync();

        hidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        hiddenBody.Should().Contain("tenant_not_authorized").And.NotContain(agentId.ToString("D"));
    }

    [Fact]
    public async Task WhoAmI_RequiresTheReadScopeInTheSignedDelegation()
    {
        using var app = await BuildAppAsync(new RecordingAccessEvaluation());
        var client = M2mClient(app);
        var tokens = app.Services.GetRequiredService<McpOperatorDelegationTokenService>();
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, tokens.Create(
            new McpOperatorDelegationIdentity("operator@example.test", "automation-client", "automation-client", [], ["Operator"], ["netratel.mcp.observe"]),
            new McpOperatorDelegationRequest("netratel_access", "whoami", "request-whoami", "https://mcp.dev.example/mcp", "dev", null, null, "correlation-whoami")));

        var response = await client.GetAsync("/api/v2/mcp/operator/access/whoami");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.Should().Contain("oauth_scope_missing").And.Contain("netratel_access/whoami");
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("summary").GetString().Should().Be("MCP operator access inspection was not admitted.");
        document.RootElement.GetProperty("failure").GetProperty("safeDetails").GetString()
            .Should().Be("The signed delegation does not include the required read scope.");
    }

    [Fact]
    public async Task AuthenticationStatus_RequiresExactSignedDelegationAndReturnsOnlyDelegatedIdentityFacts()
    {
        using var app = await BuildAppAsync(new RecordingAccessEvaluation());
        var client = M2mClient(app);

        var missing = await client.GetAsync("/api/v2/mcp/operator/auth/status");

        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("delegated_identity_required");

        var tokens = app.Services.GetRequiredService<McpOperatorDelegationTokenService>();
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, tokens.Create(
            new McpOperatorDelegationIdentity("netratel-mcp-dev-ai-agent", "netratel-dev", "netratel-dev", ["netratel-mcp-dev-consumers"], ["AutomationOperator", "Operator", "Observer", "OnboardingOperator"], ["netratel.mcp.read", "netratel.mcp.observe"]),
            new McpOperatorDelegationRequest("netratel_auth", "status", "request-auth-status", "https://mcp.dev.example/mcp", "dev", null, null, "correlation-auth-status")));

        var response = await client.GetAsync("/api/v2/mcp/operator/auth/status");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("mcp_operator_delegation")
            .And.Contain("netratel-mcp-dev-ai-agent")
            .And.Contain("AutomationOperator")
            .And.Contain("netratel.mcp.read")
            .And.NotContain("M2M");
    }

    private static HttpClient M2mClient(IHost app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("M2M");
        return client;
    }

    private static async Task<IHost> BuildAppAsync(RecordingAccessEvaluation access)
    {
        var delegationOptions = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-dev",
            Audience = "netratel-api-dev",
            ServicePrincipal = "netratel-mcp-http-dev",
            KeyId = "dev-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"))
        };
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Development);
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "M2M";
                    options.DefaultChallengeScheme = "M2M";
                }).AddScheme<AuthenticationSchemeOptions, TestM2mAuthenticationHandler>("M2M", _ => { });
                services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy =>
                {
                    policy.AddAuthenticationSchemes("M2M");
                    policy.RequireAuthenticatedUser();
                }));
                services.AddSingleton(delegationOptions);
                services.AddSingleton<McpOperatorDelegationTokenService>();
                services.AddSingleton<IMcpOperatorAccessEvaluation>(access);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseMiddleware<McpOperatorDelegationMiddleware>();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapMcpOperatorAccessEndpoints();
                    endpoints.MapMcpOperatorAuthenticationStatusEndpoints();
                });
            });
        });
        return await builder.StartAsync();
    }

    private sealed class RecordingAccessEvaluation(Guid? agentId = null, bool tenantVisible = true) : IMcpOperatorAccessEvaluation
    {
        private readonly Guid _agentId = agentId ?? Guid.NewGuid();
        private readonly bool _tenantVisible = tenantVisible;
        public List<(string Tool, string Operation)> Evaluations { get; } = [];

        public Task<McpOperatorAccessCaller> WhoAmIAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, CancellationToken cancellationToken) =>
            Task.FromResult(Caller(environment));

        public Task<McpOperatorAccessTargetResolution> ResolveTargetAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, Guid agentId, CancellationToken cancellationToken) =>
            Task.FromResult(_tenantVisible
                ? new McpOperatorAccessTargetResolution(true, Target(tenantId, agentId), [])
                : new McpOperatorAccessTargetResolution(false, null, []));

        public Task<McpOperatorEffectiveAccess?> EffectiveAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, Guid agentId, CancellationToken cancellationToken) =>
            Task.FromResult<McpOperatorEffectiveAccess?>(new McpOperatorEffectiveAccess(Caller(environment), Target(tenantId, agentId), [], [], [], "Evaluate one exact operation."));

        public Task<McpOperatorExactAccessEvaluation?> EvaluateAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, Guid agentId, string tool, string operation, string correlationId, string requestId, CancellationToken cancellationToken)
        {
            Evaluations.Add((tool, operation));
            return Task.FromResult<McpOperatorExactAccessEvaluation?>(new McpOperatorExactAccessEvaluation(
                Caller(environment),
                Target(tenantId, agentId),
                tool,
                operation,
                "netratel.mcp.observe",
                true,
                null,
                null,
                [],
                new McpOperatorConstraints(),
                "not_checked",
                ["Target capability is rechecked by the dispatch route."],
                "Invoke the exact target tool."));
        }

        public Task<McpOperatorExactAccessEvaluation?> EvaluateForPolicyAdministratorAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, Guid agentId, string tool, string operation, string correlationId, string requestId, CancellationToken cancellationToken) =>
            EvaluateAsync(environment, principal, tenantId, agentId, tool, operation, correlationId, requestId, cancellationToken);

        private static McpOperatorAccessCaller Caller(McpOperatorEnvironment environment) => new(
            "ope…st", "cod…nt", environment, [], ["Operator"], ["netratel.mcp.read"], [42], false);

        private McpOperatorAccessTarget Target(int tenantId, Guid agentId) => new(
            tenantId, agentId == Guid.Empty ? _agentId : agentId, McpOperatorTargetClassification.DevelopmentSafe, ["qa"], true, true, 1);
    }

    private sealed class TestM2mAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Request.Headers.Authorization == "M2M"
                ? Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "netratel-mcp-http-dev")], Scheme.Name)),
                    Scheme.Name)))
                : Task.FromResult(AuthenticateResult.Fail("Missing M2M authentication."));
    }
}

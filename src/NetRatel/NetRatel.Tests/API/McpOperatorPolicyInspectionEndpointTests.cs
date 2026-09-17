using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

public sealed class McpOperatorPolicyInspectionEndpointTests
{
    [Fact]
    public async Task Policy_list_requires_signed_delegation_administrator_scope_and_role_before_reading()
    {
        var administration = new RecordingPolicyAdministration();
        using var app = await BuildAppAsync(administration);
        var path = "/api/v2/mcp/operator/policy/policies?environment=Development&tenantId=42";
        var tokens = app.Services.GetRequiredService<McpOperatorDelegationTokenService>();

        var missingClient = M2mClient(app);
        var missing = await missingClient.GetAsync(path);

        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("delegated_identity_required");

        var roleMissingClient = M2mClient(app);
        roleMissingClient.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, tokens.Create(
            new McpOperatorDelegationIdentity("policy.admin@example.test", "automation-client", "automation-client", [], ["Operator"], ["netratel.mcp.admin"]),
            new McpOperatorDelegationRequest("netratel_policy", "policies", "request-role-missing", "https://mcp.dev.example/mcp", "dev", 42, null, "correlation-role-missing")));

        var roleMissing = await roleMissingClient.GetAsync(path);

        roleMissing.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await roleMissing.Content.ReadAsStringAsync()).Should().Contain("oauth_role_missing").And.Contain("netratel_policy/policies");
        administration.ListRequests.Should().BeEmpty();

        var allowedClient = M2mClient(app);
        allowedClient.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, tokens.Create(
            new McpOperatorDelegationIdentity("policy.admin@example.test", "automation-client", "automation-client", [], ["PolicyAdministrator"], ["netratel.mcp.admin"]),
            new McpOperatorDelegationRequest("netratel_policy", "policies", "request-allowed", "https://mcp.dev.example/mcp", "dev", 42, null, "correlation-allowed")));

        var allowed = await allowedClient.GetAsync(path);

        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        administration.ListRequests.Should().ContainSingle().Which.Should().Be((McpOperatorEnvironment.Development, 42));
    }

    [Fact]
    public async Task Policy_matches_returns_only_bounded_selector_inspection_for_the_signed_target()
    {
        var administration = new RecordingPolicyAdministration();
        using var app = await BuildAppAsync(administration);
        var client = M2mClient(app);
        var tokens = app.Services.GetRequiredService<McpOperatorDelegationTokenService>();
        var agentId = Guid.Parse("bb4ecc38-a8bf-4730-bbb8-3e64ecc3f21b");
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, tokens.Create(
            new McpOperatorDelegationIdentity("policy.admin@example.test", "automation-client", "automation-client", [], ["PolicyAdministrator"], ["netratel.mcp.admin"]),
            new McpOperatorDelegationRequest("netratel_policy", "matches", "request-matches", "https://mcp.dev.example/mcp", "dev", 42, agentId, "correlation-matches")));

        var response = await client.GetAsync($"/api/v2/mcp/operator/policy/matches/42/{agentId:D}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"name\":\"matching tenant policy\"")
            .And.NotContain("\"name\":\"different classification policy\"")
            .And.Contain("\"correlationId\":\"correlation-matches\"");
        administration.TargetRequests.Should().ContainSingle().Which.Should().Be((42, agentId));
        administration.ListRequests.Should().ContainSingle().Which.Should().Be((McpOperatorEnvironment.Development, 42));
    }

    [Fact]
    public async Task Policy_evaluate_uses_the_signed_administrator_without_requiring_prior_target_visibility()
    {
        var administration = new RecordingPolicyAdministration();
        var access = new RecordingPolicyAccessEvaluation();
        using var app = await BuildAppAsync(administration, access);
        var client = M2mClient(app);
        var tokens = app.Services.GetRequiredService<McpOperatorDelegationTokenService>();
        var agentId = Guid.Parse("4e3cae73-7fd2-469d-a542-fd0af85d7cc3");
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, tokens.Create(
            new McpOperatorDelegationIdentity("policy.admin@example.test", "automation-client", "automation-client", [], ["PolicyAdministrator"], ["netratel.mcp.admin"]),
            new McpOperatorDelegationRequest("netratel_policy", "evaluate", "request-evaluate", "https://mcp.dev.example/mcp", "dev", 42, agentId, "correlation-evaluate")));

        var response = await client.GetAsync($"/api/v2/mcp/operator/policy/evaluate/42/{agentId:D}?tool=netratel_terminal&operation=availability");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("target_policy_missing")
            .And.Contain("correlation-evaluate");
        access.AdministrativeEvaluations.Should().ContainSingle().Which.Should().Be(("policy.admin@example.test", 42, agentId, "netratel_terminal", "availability"));
    }

    private static HttpClient M2mClient(IHost app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("M2M");
        return client;
    }

    private static async Task<IHost> BuildAppAsync(
        RecordingPolicyAdministration administration,
        RecordingPolicyAccessEvaluation? access = null)
    {
        access ??= new RecordingPolicyAccessEvaluation();
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
                services.AddSingleton<IMcpOperatorPolicyAdministration>(administration);
                services.AddSingleton<IMcpOperatorAccessEvaluation>(access);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseMiddleware<McpOperatorDelegationMiddleware>();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapMcpOperatorPolicyInspectionEndpoints());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class RecordingPolicyAdministration : IMcpOperatorPolicyAdministration
    {
        private static readonly McpOperatorPolicy MatchingPolicy = Policy(
            "matching tenant policy",
            McpOperatorTargetClassification.ManagedStandard);
        private static readonly McpOperatorPolicy NonMatchingPolicy = Policy(
            "different classification policy",
            McpOperatorTargetClassification.Restricted);

        public List<(McpOperatorEnvironment? Environment, int? TenantId)> ListRequests { get; } = [];
        public List<(int TenantId, Guid AgentId)> TargetRequests { get; } = [];

        public Task<McpOperatorPolicyPage> ListAsync(McpOperatorEnvironment? environment, int? tenantId, CancellationToken cancellationToken)
        {
            ListRequests.Add((environment, tenantId));
            return Task.FromResult(new McpOperatorPolicyPage([MatchingPolicy, NonMatchingPolicy]));
        }

        public Task<McpOperatorPolicy?> GetAsync(Guid policyId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<McpOperatorPolicyChangeAudit>> ListChangeAuditsAsync(int? tenantId, Guid? policyId, Guid? agentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<McpOperatorAcceptedAuditPage> ListAcceptedAuditsAsync(McpOperatorAcceptedAuditFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<McpOperatorPolicy> CreateAsync(McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ValidateDraftAsync(McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<McpOperatorPolicy> ReplaceAsync(Guid policyId, long expectedVersion, McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<McpOperatorPolicy?> DisableAsync(Guid policyId, long expectedVersion, string actorId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<McpOperatorTargetProfile?> GetTargetProfileAsync(int tenantId, Guid agentId, CancellationToken cancellationToken)
        {
            TargetRequests.Add((tenantId, agentId));
            return Task.FromResult<McpOperatorTargetProfile?>(new McpOperatorTargetProfile(
                agentId,
                tenantId,
                McpOperatorTargetClassification.ManagedStandard,
                ["branch-office"],
                DateTimeOffset.UtcNow,
                "policy.admin@example.test",
                1));
        }

        public Task<McpOperatorTargetProfile> UpsertTargetProfileAsync(int tenantId, Guid agentId, McpOperatorTargetClassification classification, IReadOnlyCollection<string> tags, long? expectedVersion, string actorId, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static McpOperatorPolicy Policy(string name, McpOperatorTargetClassification classification) => new(
            Guid.NewGuid(),
            name,
            McpOperatorEnvironment.Development,
            McpOperatorPolicyEffect.Allow,
            10,
            new McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind.OidcGroup, "incident-responders"),
            new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.Tenant, 42),
            classification,
            McpOperatorOperationFamily.Observability,
            null,
            new McpOperatorConstraints(),
            DateTimeOffset.UtcNow,
            "policy.admin@example.test",
            null,
            null,
            null,
            null,
            1);
    }

    private sealed class RecordingPolicyAccessEvaluation : IMcpOperatorAccessEvaluation
    {
        public List<(string Subject, int TenantId, Guid AgentId, string Tool, string Operation)> AdministrativeEvaluations { get; } = [];

        public Task<McpOperatorAccessCaller> WhoAmIAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<McpOperatorAccessTargetResolution> ResolveTargetAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, Guid agentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<McpOperatorEffectiveAccess?> EffectiveAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, Guid agentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<McpOperatorExactAccessEvaluation?> EvaluateAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, Guid agentId, string tool, string operation, string correlationId, string requestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<McpOperatorExactAccessEvaluation?> EvaluateForPolicyAdministratorAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, Guid agentId, string tool, string operation, string correlationId, string requestId, CancellationToken cancellationToken)
        {
            AdministrativeEvaluations.Add((principal.Subject, tenantId, agentId, tool, operation));
            return Task.FromResult<McpOperatorExactAccessEvaluation?>(new McpOperatorExactAccessEvaluation(
                new McpOperatorAccessCaller("pol…st", "cod…nt", environment, [], ["PolicyAdministrator"], ["netratel.mcp.admin"], [], false),
                new McpOperatorAccessTarget(tenantId, agentId, McpOperatorTargetClassification.ManagedStandard, [], true, true, 1),
                tool,
                operation,
                "netratel.mcp.observe",
                false,
                "target_policy_missing",
                McpOperatorAuthorizationLayer.Policy,
                [],
                null,
                "not_checked",
                [],
                "Create or update a reviewed policy for the signed administrator."));
        }
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

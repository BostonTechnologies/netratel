using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorTenantEndpointTests
{
    [Fact]
    public void Production_tenant_routes_are_control_plane_versioned_and_m2m_only()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(new McpOperatorLocalAgentOptions());
        builder.Services.AddSingleton<IMcpOperatorAuthorization>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorConfirmationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<ITenantService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<OrchestratorDbContext>(_ => throw new NotSupportedException());
        var app = builder.Build();

        app.MapMcpOperatorTenantEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/tenants", StringComparison.Ordinal) is true)
            .ToArray();

        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/tenants/",
            "/api/v2/mcp/operator/tenants/{tenantId:int}",
            "/api/v2/mcp/operator/tenants/preview/{action}",
            "/api/v2/mcp/operator/tenants/confirm/{action}");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }

    [Fact]
    public async Task Active_tenant_delete_requires_a_separate_control_plane_policy_approval()
    {
        var denied = await BuildAppAsync(allowActiveTenantDeletion: false);
        using (denied.App)
        {
            var response = await PreviewDeleteAsync(denied.App, tenantId: 42);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await response.Content.ReadAsStringAsync()).Should().Contain("active_caller_tenant_delete_requires_separate_approval");
            denied.Tenants.GetCalls.Should().Be(0);
            denied.Confirmations.PlanRequests.Should().BeEmpty();
        }

        var approved = await BuildAppAsync(allowActiveTenantDeletion: true);
        using (approved.App)
        {
            var response = await PreviewDeleteAsync(approved.App, tenantId: 42);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            approved.Tenants.GetCalls.Should().Be(1);
            approved.Confirmations.PlanRequests.Should().ContainSingle();
        }
    }

    private static async Task<HttpResponseMessage> PreviewDeleteAsync(IHost app, int tenantId)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("M2M");
        var assertion = app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
            new McpOperatorDelegationIdentity("tenant.admin@example.test", "tenant-admin-client", null, [], ["Administrator"], ["netratel.mcp.admin"], tenantId),
            new McpOperatorDelegationRequest("netratel_tenants", "delete", "request-delete", "https://mcp.prod.example/mcp", "prod", null, null, "correlation-delete"));
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);
        return await client.PostAsJsonAsync("/api/v2/mcp/operator/tenants/preview/delete", new { tenantId, expectedVersion = 1, cascade = false });
    }

    private static async Task<TenantEndpointHarness> BuildAppAsync(bool allowActiveTenantDeletion)
    {
        var delegationOptions = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-test",
            Audience = "netratel-api-test",
            ServicePrincipal = "netratel-mcp-http-test",
            KeyId = "test-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"))
        };
        var tenants = new RecordingTenantService();
        var confirmations = new RecordingConfirmations();
        var authorization = new RecordingAuthorization(allowActiveTenantDeletion);
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Production);
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
                services.AddSingleton(new McpOperatorLocalAgentOptions());
                services.AddSingleton<IMcpOperatorAuthorization>(authorization);
                services.AddSingleton<IMcpOperatorConfirmationService>(confirmations);
                services.AddSingleton<ITenantService>(tenants);
                services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase($"operator-tenants-{Guid.NewGuid():N}"));
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseMiddleware<McpOperatorDelegationMiddleware>();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapMcpOperatorTenantEndpoints());
            });
        });
        return new(await builder.StartAsync(), tenants, confirmations);
    }

    private sealed record TenantEndpointHarness(IHost App, RecordingTenantService Tenants, RecordingConfirmations Confirmations);

    private sealed class RecordingAuthorization(bool allowActiveTenantDeletion) : IMcpOperatorAuthorization
    {
        private static readonly Guid PolicyId = Guid.Parse("dfe3ebfb-2af0-47c6-b4f5-6e26f2346f4d");

        public Task<bool> HasTenantVisibilityAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<McpOperatorDecision> EvaluateAsync(McpOperatorAccessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new McpOperatorDecision(true, null, null, [PolicyId], new McpOperatorConstraints(AllowActiveTenantDeletion: allowActiveTenantDeletion), request.TargetSetDigest, request, 1));

        public Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorDecision decision, string servicePrincipal, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This safety test only previews tenant deletion.");
    }

    private sealed class RecordingConfirmations : IMcpOperatorConfirmationService
    {
        public List<McpOperatorConfirmationPlanRequest> PlanRequests { get; } = [];

        public Task<McpOperatorConfirmationPlan> CreatePlanAsync(McpOperatorConfirmationPlanRequest request, CancellationToken cancellationToken)
        {
            PlanRequests.Add(request);
            return Task.FromResult(new McpOperatorConfirmationPlan(
                "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
                "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0",
                DateTimeOffset.UtcNow.AddMinutes(5),
                McpOperatorConfirmationClass.Destructive,
                request.PayloadHash,
                request.Decision.TargetSetDigest));
        }

        public Task<McpOperatorConfirmationAdmission> ConfirmAsync(McpOperatorConfirmationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CompleteAsync(Guid idempotencyId, McpOperatorIdempotencyOutcome outcome, string? resultReference, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingTenantService : ITenantService
    {
        private readonly TenantInfo _tenant = new(42, "Camelot", null, null, ["camelot.example"], null, null, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Version: 1);
        public int GetCalls { get; private set; }

        public Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TenantInfo?> GetAsync(int tenantId, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult<TenantInfo?>(tenantId == _tenant.TenantId ? _tenant : null);
        }
        public Task<bool> ExistsAsync(int tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TenantInfo> CreateAsync(CreateTenantCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TenantInfo?> UpdateAsync(UpdateTenantCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TenantInfo?> DeleteAsync(int tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TenantInfo?> DeleteAsync(int tenantId, long expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
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
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "netratel-mcp-http-test")], Scheme.Name)),
                    Scheme.Name)))
                : Task.FromResult(AuthenticateResult.Fail("Missing M2M authentication."));
    }
}

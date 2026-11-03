using System.Collections.Immutable;
using System.Threading.RateLimiting;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Security.Integration;
using NetRatel.API.Services.Monitoring;
using NetRatel.Application.Agents;
using NetRatel.Application.Flows;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Http;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Operations;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed partial class ReviewedMcpAccessProtocolTests
{
    [Theory]
    [InlineData(17)]
    [InlineData(29)]
    public async Task Existing_unrestricted_production_credential_reads_unprofiled_offline_client_through_http_ingress(int tenant)
    {
        const string environment = "Production", instance = "prod";
        await using var fixture = await ReviewedMcpAccessTests.Fixture.CreateAsync();
        var agent = Guid.NewGuid();
        fixture.App.Agents.Add(new Agent { Id = agent, TenantId = tenant, IsEnabled = true, Status = AgentStatus.Active });
        await fixture.App.SaveChangesAsync();
        var role = await fixture.Identity.AccessRoles.Include(x => x.Permissions).SingleAsync();
        role.Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.MonitoringRead });
        var credential = await fixture.Identity.IntegrationCredentials.SingleAsync();
        credential.Grants.Add(new IntegrationCredentialGrant { CredentialId = credential.Id, TenantId = tenant, Permission = NetRatelPermissions.MonitoringRead });
        await fixture.Identity.SaveChangesAsync();
        await new McpOperatorPolicyAdministration(fixture.App).CreateAsync(new("Existing unrestricted tenant access", McpOperatorEnvironment.Production,
            McpOperatorPolicyEffect.Allow, 0, new(McpOperatorPrincipalSelectorKind.OAuthClientId, credential.Id),
            new(McpOperatorTargetSelectorKind.Tenant, tenant), null, McpOperatorOperationCatalog.Find("netratel_monitoring", "client_series")!.OperationFamily, null,
            new McpOperatorConstraints(), DateTimeOffset.UtcNow.AddDays(1), null), "reviewer", default);
        var key = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("reviewed-protocol-key-at-least-thirty-two-bytes"));
        var options = new McpOperatorDelegationOptions { Enabled = true, Issuer = "netratel-mcp-dev", Audience = "netratel-api-dev", ServicePrincipal = "netratel-mcp-http-dev", KeyId = "reviewed-test", SharedKeyBase64 = key };
        var apiBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        apiBuilder.WebHost.UseTestServer();
        apiBuilder.Configuration["Integration:HttpMcp:PublicResourceUri"] = ReviewedMcpAccessTests.Fixture.Resource;
        apiBuilder.Services.Configure<DeploymentBrandingOptions>(branding => branding.SiteUrl = ReviewedMcpAccessTests.Fixture.Origin);
        apiBuilder.Services.AddScoped<IDeploymentBrandingService, DeploymentBrandingService>();
        apiBuilder.Services.AddScoped<HttpHostSetupService>();
        apiBuilder.Services.AddSingleton(fixture.Identity); apiBuilder.Services.AddSingleton(fixture.App); apiBuilder.Services.AddSingleton(fixture.Pairing);
        apiBuilder.Services.AddSingleton<IIntegrationCredentialService>(fixture.Credentials); apiBuilder.Services.AddSingleton<IIntegrationCredentialCurrentVerifier>(fixture.Credentials);
        apiBuilder.Services.AddSingleton<IEffectiveAccessService>(fixture.EffectiveAccess);
        apiBuilder.Services.AddSingleton<TimeProvider>(fixture.Clock);
        apiBuilder.Services.AddSingleton(options); apiBuilder.Services.AddSingleton(new McpOperatorDelegationTokenService(options));
        apiBuilder.Services.AddSingleton<IFlowDefinitionService, FlowPersistenceService>();
        var cache = ImmutableArray.Create(new MonitoringSeriesState(new(tenant, Guid.NewGuid(), agent, "cpu"), 1, 1, MonitoringPhase.Healthy, MonitoringEvidenceQuality.Unknown));
        var runtime = Stub<IMonitoringRuntime>((method, args) => method == "GetClientAsync" ? Task.FromResult(cache) : throw new NotSupportedException(method));
        var agents = Stub<IAgentManagementService>((method, args) => method == "GetAsync" ? Task.FromResult<AgentDetailDto?>(new(tenant, agent, "Synthetic worker", true, null, DateTimeOffset.UtcNow, null, null, null, null)) : throw new NotSupportedException(method));
        apiBuilder.Services.AddSingleton(new MonitoringApiService(new MonitoringResourceAuthorizer(fixture.EffectiveAccess), fixture.EffectiveAccess,
            null!, null!, runtime, null!, agents, null!, null!, null!, null!, null!, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<MonitoringApiService>.Instance));
        apiBuilder.Services.AddSingleton(services => new ManagedOperationAdmission(fixture.EffectiveAccess, null!, fixture.Authorization, new McpOperatorRouteAdmissionService(fixture.App, fixture.Authorization), fixture.App, new OfflinePresence(), services.GetRequiredService<IHostEnvironment>()));
        apiBuilder.Services.AddAuthentication("ReviewedTest").AddScheme<AuthenticationSchemeOptions, GatewayAuthentication>("ReviewedTest", _ => { })
            .AddScheme<AuthenticationSchemeOptions, IntegrationCredentialAuthenticationHandler>(IntegrationCredentialAuthenticationHandler.SchemeName, _ => { });
        apiBuilder.Services.AddAuthorization(auth =>
        {
            auth.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser());
            auth.AddPolicy("InteractiveAccount", policy => policy.RequireAuthenticatedUser().RequireClaim("netratel_principal_id"));
            auth.AddPolicy(ManagedOperationAuthorization.PolicyName, policy => policy.RequireAuthenticatedUser());
            auth.AddPolicy("McpLocalDelegationExchange", policy => policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.SchemeName).RequireAuthenticatedUser());
        });
        apiBuilder.Services.AddRateLimiter(limits =>
        {
            foreach (var name in new[] { "integration-pairing-start", "integration-pairing-poll", "integration-pairing-account" })
                limits.AddPolicy(name, _ => RateLimitPartition.GetNoLimiter("test"));
        });
        await using var api = apiBuilder.Build();
        api.UseAuthentication(); api.UseMiddleware<McpOperatorDelegationMiddleware>(); api.UseAuthorization(); api.UseRateLimiter();
        api.MapMcpLocalDelegationEndpoints(); api.MapMcpReviewedAccessEndpoints(); api.MapManagedOperationEndpoints(); api.MapIntegrationPairingEndpoints();
        await api.StartAsync();
        var configPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new NetRatel.AgentClient.AgentClientConfiguration("https://api.example.test", "https://issuer.example.test/token", "gateway", "agent", "synthetic", "mcp:read"), NetRatel.AgentClient.AgentClientConfiguration.JsonOptions));
        try
        {
            var mcpBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
            mcpBuilder.WebHost.UseTestServer();
            mcpBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatel:Mcp:Http:PublicResourceUri"] = ReviewedMcpAccessTests.Fixture.Resource,
                ["NetRatel:Mcp:Http:AllowedOrigins:0"] = "https://operator.example.test",
                ["NetRatel:Mcp:Http:Instance"] = instance, ["NetRatel:Mcp:Http:OperatorSurfaceEnabled"] = "true",
                ["NetRatel:Mcp:Http:ConfigurationPath"] = configPath,
                ["NetRatel:Mcp:Http:DevApiBaseUrl"] = instance == "dev" ? "https://api.example.test" : "https://dev-api.example.test",
                ["NetRatel:Mcp:Http:ProdApiBaseUrl"] = instance == "prod" ? "https://api.example.test" : "https://prod-api.example.test",
                ["NetRatel:Mcp:Http:LocalCredentialMode"] = "true",
                ["NetRatel:Mcp:Delegation:Enabled"] = "true", ["NetRatel:Mcp:Delegation:Issuer"] = options.Issuer,
                ["NetRatel:Mcp:Delegation:Audience"] = options.Audience, ["NetRatel:Mcp:Delegation:ServicePrincipal"] = options.ServicePrincipal,
                ["NetRatel:Mcp:Delegation:KeyId"] = options.KeyId, ["NetRatel:Mcp:Delegation:SharedKeyBase64"] = key
            });
            NetRatelMcpHttpApplication.ConfigureServices(mcpBuilder);
            mcpBuilder.Services.AddHttpClient(McpLocalCredentialAuthenticationHandler.ApiHttpClientName).ConfigurePrimaryHttpMessageHandler(() => api.GetTestServer().CreateHandler());
            mcpBuilder.Services.AddSingleton<INetRatelMcpApiClient>(services => new ApiForwarder(api.GetTestClient(), services.GetRequiredService<IMcpOperatorDelegationContext>()));
            await using var mcp = mcpBuilder.Build(); NetRatelMcpHttpApplication.ConfigurePipeline(mcp); await mcp.StartAsync();
            using var client = mcp.GetTestClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Credential.Secret);
            var result = await CallAsync(client, "netratel_monitoring", "client_series", new { tenantId = tenant, agentId = agent });
            Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
            Assert.Contains("cpu", result.GetProperty("data").GetRawText());
            Assert.Single(await fixture.App.McpOperatorAcceptedAudits.ToArrayAsync());
            var inspection = await fixture.Pairing.InspectCredentialAdmissionAsync("requester", credential.Id, McpOperatorEnvironment.Production,
                [new(tenant, "netratel_monitoring", "client_series", agent)]);
            var checkedOperation = Assert.Single(inspection!.EffectiveAdmission);
            Assert.True(checkedOperation.AuthorizationAdmitted); Assert.True(checkedOperation.PolicyAllowed);
            Assert.Equal("not_checked", checkedOperation.TargetReadiness); Assert.Equal("not_checked", checkedOperation.CapabilityAvailability);
            agent = Guid.NewGuid();
            fixture.App.Agents.Add(new Agent { Id = agent, TenantId = tenant, IsEnabled = true, Status = AgentStatus.Active });
            await fixture.App.SaveChangesAsync();
            cache = ImmutableArray.Create(new MonitoringSeriesState(new(tenant, Guid.NewGuid(), agent, "new-client-cpu"), 1, 1, MonitoringPhase.Healthy, MonitoringEvidenceQuality.Unknown));
            var newlyEnrolled = await CallAsync(client, "netratel_monitoring", "client_series", new { tenantId = tenant, agentId = agent });
            Assert.True(newlyEnrolled.GetProperty("success").GetBoolean(), newlyEnrolled.GetRawText());
            Assert.Contains("new-client-cpu", newlyEnrolled.GetProperty("data").GetRawText());
            Assert.Empty(await fixture.App.McpOperatorTargetProfiles.ToArrayAsync());
            Assert.Equal(credential.ExpiresAtUtc, (await fixture.Identity.IntegrationCredentials.SingleAsync()).ExpiresAtUtc);
        }
        finally { File.Delete(configPath); }
    }

    private sealed class OfflinePresence : IClientPresenceReadModel
    {
        public Task<ClientPresenceReadModelSnapshot> GetSnapshotAsync(CancellationToken ct) => Task.FromResult(new ClientPresenceReadModelSnapshot(0, []));
        public Task<ClientPresenceSnapshot?> GetClientSnapshotAsync(ClientKey client, CancellationToken ct) => Task.FromResult<ClientPresenceSnapshot?>(null);
    }
    private static T Stub<T>(Func<string, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, ProtocolStub>();
        ((ProtocolStub)(object)proxy).Call = invoke;
        return proxy;
    }
    public class ProtocolStub : DispatchProxy
    {
        public Func<string, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!.Name, args);
    }
}

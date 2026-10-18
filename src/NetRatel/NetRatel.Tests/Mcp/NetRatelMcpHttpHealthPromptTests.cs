using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Middleware;
using NetRatel.AgentClient;
using NetRatel.Mcp.Core;
using NetRatel.Shared.Operations;
using NetRatel.Web.Configuration;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed partial class NetRatelMcpHttpTests
{
    [Fact]
    public async Task Prompt_metadata_and_nonstring_argument_validation_agree()
    {
        await using var host = await CreateApplicationAsync();
        using var client = host.Application.GetTestClient();
        using var listed = await client.SendAsync(McpRequest(87, "prompts/list", new { }));
        using var metadata = await ReadMcpPayloadAsync(listed);
        foreach (var pair in new[] { (Prompt: "inspect_client", Argument: "client"), (Prompt: "diagnose_job_run", Argument: "jobRunId") })
        {
            var prompt = metadata.RootElement.GetProperty("result").GetProperty("prompts").EnumerateArray().Single(item => item.GetProperty("name").GetString() == pair.Prompt);
            var argument = prompt.GetProperty("arguments").EnumerateArray().Single(item => item.GetProperty("name").GetString() == pair.Argument);
            argument.GetProperty("required").GetBoolean().Should().BeTrue();
            argument.GetProperty("description").GetString().Should().Contain("string");
            using var response = await client.SendAsync(McpRequest(88, "prompts/get", new { name = pair.Prompt, arguments = new Dictionary<string, object> { [pair.Argument] = 123 } }));
            using var payload = await ReadMcpPayloadAsync(response);
            payload.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602);
            payload.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Contain(pair.Argument);
        }
    }

    internal static async Task VerifyHealthThroughProtectedWebGatewayAsync()
    {
        var apiBuilder = WebApplication.CreateBuilder();
        apiBuilder.Logging.ClearProviders();
        apiBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        apiBuilder.Services.AddAuthentication("health-fixture").AddScheme<AuthenticationSchemeOptions, HealthFixtureAuthentication>("health-fixture", _ => { });
        apiBuilder.Services.AddAuthorization(options => options.AddPolicy("HealthRead", policy =>
            policy.RequireAuthenticatedUser().RequireClaim("synthetic-health-permission", "read")));
        apiBuilder.Services.AddSingleton(LocalCredentialDelegationOptions());
        apiBuilder.Services.AddSingleton<McpOperatorDelegationTokenService>();
        var backendReady = true;
        apiBuilder.Services.AddHealthChecks().AddCheck("synthetic-dependency", () => backendReady
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy()
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("private dependency cause"));
        await using var api = apiBuilder.Build();
        api.UseAuthentication();
        api.UseMiddleware<McpOperatorDelegationMiddleware>();
        api.UseAuthorization();
        var admittedRequests = new ConcurrentQueue<(string Path, string Authorization, string? Tool, string? Operation)>();
        api.Use(async (context, next) =>
        {
            context.TryGetMcpOperatorDelegation(out var delegation);
            admittedRequests.Enqueue((context.Request.Path.Value!, context.Request.Headers.Authorization.ToString(),
                delegation?.Tool, delegation?.Operation));
            await next(context);
        });
        api.MapHealthEndpoints();
        await api.StartAsync();
        var apiAddress = api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = apiAddress }).Build();
        using var gatewayConfiguration = WebGatewayConfiguration.Create(config, WebGatewayOptions.Resolve(config));
        var gatewayBuilder = WebApplication.CreateBuilder();
        gatewayBuilder.Logging.ClearProviders();
        gatewayBuilder.WebHost.UseTestServer();
        gatewayBuilder.Services.AddReverseProxy().LoadFromConfig(gatewayConfiguration.GetSection("ReverseProxy"));
        await using var gateway = gatewayBuilder.Build();
        var gatewayUnavailable = false;
        var invalidReadinessPayload = false;
        var rootHealthUnmapped = false;
        var operatorHealthUnmapped = false;
        var gatewayRequests = new ConcurrentQueue<string>();
        gateway.Use(async (context, next) =>
        {
            gatewayRequests.Enqueue(context.Request.Path.Value!);
            if (rootHealthUnmapped && context.Request.Path.StartsWithSegments("/health") ||
                operatorHealthUnmapped && context.Request.Path.StartsWithSegments("/api/v2/mcp/operator/health"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (gatewayUnavailable && context.Request.Path == "/api/v2/mcp/operator/health/ready")
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { title = "Synthetic gateway unavailable" });
                return;
            }
            if (invalidReadinessPayload && context.Request.Path == "/api/v2/mcp/operator/health/ready")
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{ private malformed upstream response");
                return;
            }
            await next(context);
        });
        gateway.MapReverseProxy();
        await gateway.StartAsync();
        using var destination = gateway.GetTestClient();
        foreach (var path in new[] { "/health/live", "/health/ready", "/api/v2/mcp/operator/health/live", "/api/v2/mcp/operator/health/ready" })
        {
            destination.DefaultRequestHeaders.Authorization = null;
            using var anonymous = await destination.GetAsync(path);
            anonymous.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized, "every alias must preserve API HealthRead authorization");
            destination.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-unprivileged-identity");
            using var forbidden = await destination.GetAsync(path);
            forbidden.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
            destination.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-health-identity");
            using var allowed = await destination.GetAsync(path);
            allowed.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, "the original health routes remain compatible");
        }
        admittedRequests.Clear();
        gatewayRequests.Clear();
        rootHealthUnmapped = true;
        using var exchange = new ContractLocalExchange();
        await using var mcp = await CreateApplicationAsync(localCredentialMode: true, localCredentialApiHandler: exchange,
            operatorSurfaceEnabled: true, configureServices: services =>
                services.AddHttpClient(NetRatelMcpOutboundClient.ApiHttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => gateway.GetTestServer().CreateHandler()));
        // Only the synthetic token source and network destination are substituted: the MCP
        // HTTP adapter, outbound request construction, delegation and YARP forwarding are real.
        await mcp.Application.Services.GetRequiredService<NetRatelMcpAccessTokenCache>().GetAsync(
            _ => Task.FromResult(new NetRatelMcpAccessToken("synthetic-health-identity", DateTimeOffset.UtcNow.AddMinutes(5))),
            CancellationToken.None);
        using var client = mcp.Application.GetTestClient();
        using var response = await client.SendAsync(ContractCall("netratel_health", new { operation = "get" }));
        using var payload = await ReadMcpPayloadAsync(response);
        var result = payload.RootElement.GetProperty("result").GetProperty("structuredContent");
        result.GetProperty("success").GetBoolean().Should().BeTrue();
        var components = result.GetProperty("data").GetProperty("components");
        components.GetProperty("liveness").GetProperty("status").GetString().Should().Be("healthy");
        components.GetProperty("readiness").GetProperty("status").GetString().Should().Be("healthy");
        components.GetProperty("authentication").GetProperty("status").GetString().Should().Be("admitted");
        components.GetProperty("authentication").GetProperty("scope").GetString().Should().Be("protected_health_route");
        gatewayRequests.Should().BeEquivalentTo("/api/v2/mcp/operator/health/live", "/api/v2/mcp/operator/health/ready");
        admittedRequests.Should().HaveCount(2).And.OnlyContain(request =>
            request.Authorization == "Bearer synthetic-health-identity" && request.Tool == "netratel_health" && request.Operation == "get",
            "the service bearer and validated caller attribution must survive both outbound and gateway HTTP boundaries");
        backendReady = false;
        using var unreadyResponse = await client.SendAsync(ContractCall("netratel_health", new { operation = "get" }));
        using var unready = await ReadMcpPayloadAsync(unreadyResponse);
        var unreadyComponents = unready.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data").GetProperty("components");
        unreadyComponents.GetProperty("liveness").GetProperty("status").GetString().Should().Be("healthy");
        unreadyComponents.GetProperty("readiness").GetProperty("status").GetString().Should().Be("unready");
        unready.RootElement.GetRawText().Should().NotContain("private dependency cause");
        gatewayUnavailable = true;
        using var unavailableResponse = await client.SendAsync(ContractCall("netratel_health", new { operation = "get" }));
        using var unavailable = await ReadMcpPayloadAsync(unavailableResponse);
        var unavailableReadiness = unavailable.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data").GetProperty("components").GetProperty("readiness");
        unavailableReadiness.GetProperty("status").GetString().Should().Be("unavailable");
        unavailableReadiness.GetProperty("code").GetString().Should().Be("health_probe_readiness_503_backend_cause_unknown");
        gatewayUnavailable = false;
        invalidReadinessPayload = true;
        using var invalidResponse = await client.SendAsync(ContractCall("netratel_health", new { operation = "get" }));
        using var invalid = await ReadMcpPayloadAsync(invalidResponse);
        var invalidReadiness = invalid.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data").GetProperty("components").GetProperty("readiness");
        invalidReadiness.GetProperty("status").GetString().Should().Be("invalid_response");
        invalidReadiness.GetProperty("code").GetString().Should().Be("health_probe_invalid_response");
        invalid.RootElement.GetRawText().Should().NotContain("private malformed upstream response");
        invalidReadinessPayload = false;
        backendReady = true;
        rootHealthUnmapped = false;
        operatorHealthUnmapped = true;
        gatewayRequests.Clear();
        using var unmappedResponse = await client.SendAsync(ContractCall("netratel_health", new { operation = "get" }));
        using var unmapped = await ReadMcpPayloadAsync(unmappedResponse);
        var unmappedComponents = unmapped.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data").GetProperty("components");
        foreach (var name in new[] { "liveness", "readiness" })
        {
            unmappedComponents.GetProperty(name).GetProperty("status").GetString().Should().Be("unmapped");
            unmappedComponents.GetProperty(name).GetProperty("httpStatus").GetInt32().Should().Be(404);
        }
        unmappedComponents.GetProperty("authentication").GetProperty("status").GetString().Should().Be("unknown");
        gatewayRequests.Should().BeEquivalentTo(new[] { "/api/v2/mcp/operator/health/live", "/api/v2/mcp/operator/health/ready" },
            "an unmapped reviewed route must not trigger retries or fall back to another health surface");
    }

    [Fact]
    public async Task Inspect_client_guidance_supports_paginated_tenant_bound_discovery_without_mutation()
    {
        var discovery = new PromptDiscoveryApi();
        using var exchange = new ContractLocalExchange();
        await using var host = await CreateApplicationAsync(localCredentialMode: true, localCredentialApiHandler: exchange,
            businessApi: discovery, operatorSurfaceEnabled: true);
        using var client = host.Application.GetTestClient();
        using var promptRequest = McpRequest(85, "prompts/get", new { name = "inspect_client", arguments = new { client = "synthetic-worker" } });
        promptRequest.Headers.Authorization = new("Bearer", "nrt_ic_synthetic-contract-fixture");
        using var prompt = await client.SendAsync(promptRequest);
        using var promptPayload = await ReadMcpPayloadAsync(prompt);
        var guidance = promptPayload.RootElement.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString()!;
        guidance.IndexOf("netratel_search/clients", StringComparison.Ordinal).Should().BeLessThan(guidance.IndexOf("netratel_access", StringComparison.Ordinal));
        guidance.Should().Contain("ambiguous").And.Contain("no match").And.Contain("denied");
        using var tenants = await Read("netratel_search", new { operation = "tenants", request = new { q = "synthetic", offset = 0, limit = 1 } });
        var page = tenants.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data");
        page.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        using var continuation = await Read("netratel_search", new { operation = "tenants", request = new { q = "synthetic", offset = page.GetProperty("nextOffset").GetInt32(), limit = 1 } });
        continuation.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data").GetProperty("items")[0].GetProperty("tenantId").GetInt32().Should().Be(29);
        using var clients = await Read("netratel_search", new { operation = "clients", request = new { q = "synthetic-worker" } });
        var candidates = clients.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data").GetProperty("items");
        candidates.GetArrayLength().Should().Be(2, "duplicate names across tenants require an explicit tenant selection");
        // Synthetic caller clarification selects tenant 29; carry the pair from its result.
        var selected = candidates.EnumerateArray().Single(item => item.GetProperty("tenantId").GetInt32() == 29);
        var target = new { tenantId = selected.GetProperty("tenantId").GetInt32(), agentId = selected.GetProperty("agentId").GetString()! };
        using var access = await Read("netratel_access", new { operation = "target", request = target });
        using var effective = await Read("netratel_access", new { operation = "effective", request = target });
        using var evaluated = await Read("netratel_access", new { operation = "evaluate", request = new { target.tenantId, target.agentId, tool = "netratel_clients", operation = "presence" } });
        evaluated.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("success").GetBoolean().Should().BeTrue();
        using var presence = await Read("netratel_clients", new { operation = "presence", request = target });
        presence.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("success").GetBoolean().Should().BeTrue();
        discovery.Reads.Should().Contain(path => path.Contains("/29/22222222-2222-4222-8222-222222222222", StringComparison.Ordinal));
        discovery.Reads.Should().NotContain(path => path.Contains("/29/11111111-1111-4111-8111-111111111111", StringComparison.Ordinal));
        using var absent = await Read("netratel_search", new { operation = "clients", request = new { q = "no-match" } });
        absent.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(0);
        discovery.Deny = true;
        using var denied = await Read("netratel_search", new { operation = "clients", request = new { q = "synthetic-worker" } });
        denied.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("success").GetBoolean().Should().BeFalse();

        async Task<JsonDocument> Read(string tool, object arguments)
        {
            using var response = await client.SendAsync(ContractCall(tool, arguments));
            return await ReadMcpPayloadAsync(response);
        }
    }

    private sealed class HealthFixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            List<Claim> claims = [new("sub", "synthetic-health")];
            if (Request.Headers.Authorization == "Bearer synthetic-health-identity")
                claims.Add(new("synthetic-health-permission", "read"));
            else if (Request.Headers.Authorization != "Bearer synthetic-unprivileged-identity")
                return Task.FromResult(AuthenticateResult.NoResult());
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class PromptDiscoveryApi : INetRatelMcpApiClient
    {
        public List<string> Reads { get; } = [];
        public bool Deny { get; set; }
        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            Reads.Add(path);
            if (Deny) throw new NetRatelMcpApiException("upstream_forbidden", "Synthetic discovery denied", 403, false, "tenant_not_authorized");
            if (path.Contains("/search/tenants", StringComparison.Ordinal)) return Task.FromResult<JsonNode?>(JsonSerializer.SerializeToNode(new
            {
                items = new[] { new { tenantId = path.Contains("offset=1", StringComparison.Ordinal) ? 29 : 17, name = "synthetic" } },
                hasMore = !path.Contains("offset=1", StringComparison.Ordinal), nextOffset = 1
            }));
            if (path.Contains("/search/clients", StringComparison.Ordinal)) return Task.FromResult<JsonNode?>(JsonSerializer.SerializeToNode(new
            {
                items = path.Contains("no-match", StringComparison.Ordinal) ? [] : new[]
                {
                    new { tenantId = 17, agentId = "11111111-1111-4111-8111-111111111111", hostName = "synthetic-worker" },
                    new { tenantId = 29, agentId = "22222222-2222-4222-8222-222222222222", hostName = "synthetic-worker" }
                }, hasMore = false
            }));
            return Task.FromResult<JsonNode?>(new JsonObject { ["allowed"] = true });
        }
        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The inspect_client workflow must never dispatch a mutation.");
    }
}

public sealed class NetRatelMcpHttpHealthRouteTests
{
    [Fact]
    public Task Protocol_health_reaches_protected_API_through_the_shipped_Web_gateway()
        // This protocol-only regression does not need the PostgreSQL fixture used by
        // other HTTP MCP tests that exercise persisted credentials.
        => NetRatelMcpHttpTests.VerifyHealthThroughProtectedWebGatewayAsync();
}

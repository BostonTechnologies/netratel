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
using NetRatel.Mcp.Core;
using NetRatel.Web.Configuration;
using System.Net.Http.Json;
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

    [Fact]
    public async Task Protocol_health_reaches_protected_API_through_the_shipped_Web_gateway()
    {
        var apiBuilder = WebApplication.CreateBuilder();
        apiBuilder.Logging.ClearProviders();
        apiBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        apiBuilder.Services.AddAuthentication("health-fixture").AddScheme<AuthenticationSchemeOptions, HealthFixtureAuthentication>("health-fixture", _ => { });
        apiBuilder.Services.AddAuthorization(options => options.AddPolicy("HealthRead", policy => policy.RequireAuthenticatedUser()));
        var backendReady = true;
        apiBuilder.Services.AddHealthChecks().AddCheck("synthetic-dependency", () => backendReady
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy()
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("private dependency cause"));
        await using var api = apiBuilder.Build();
        api.UseAuthentication();
        api.UseAuthorization();
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
        gateway.Use(async (context, next) =>
        {
            if (gatewayUnavailable && context.Request.Path == "/health/ready")
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { title = "Synthetic gateway unavailable" });
                return;
            }
            await next(context);
        });
        gateway.MapReverseProxy();
        await gateway.StartAsync();
        using var destination = gateway.GetTestClient();
        using var rejected = await destination.GetAsync("/health/live");
        rejected.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized, "the gateway must preserve API HealthRead authorization");
        destination.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-health-identity");
        using var exchange = new ContractLocalExchange();
        await using var mcp = await CreateApplicationAsync(localCredentialMode: true, localCredentialApiHandler: exchange,
            businessApi: new HealthGatewayApi(destination), operatorSurfaceEnabled: true);
        using var client = mcp.Application.GetTestClient();
        using var response = await client.SendAsync(ContractCall("netratel_health", new { operation = "get" }));
        using var payload = await ReadMcpPayloadAsync(response);
        var result = payload.RootElement.GetProperty("result").GetProperty("structuredContent");
        result.GetProperty("success").GetBoolean().Should().BeTrue();
        var components = result.GetProperty("data").GetProperty("components");
        components.GetProperty("liveness").GetProperty("status").GetString().Should().Be("healthy");
        components.GetProperty("readiness").GetProperty("status").GetString().Should().Be("healthy");
        components.GetProperty("authentication").GetProperty("scope").GetString().Should().Be("protected_health_route");
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

    private sealed class HealthGatewayApi(HttpClient client) : INetRatelMcpApiClient
    {
        public async Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            using var response = await client.GetAsync(path, cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new NetRatelMcpApiException("upstream", "Safe synthetic failure", (int)response.StatusCode,
                (int)response.StatusCode >= 500, body?["code"]?.GetValue<string>() == "api_readiness_unhealthy" ? "api_readiness_unhealthy" : null);
            return body;
        }
        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Health must never mutate.");
    }

    private sealed class HealthFixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.Authorization == "Bearer synthetic-health-identity"
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "synthetic-health")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
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

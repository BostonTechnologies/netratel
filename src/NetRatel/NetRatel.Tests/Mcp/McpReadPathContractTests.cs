using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NetRatel.AgentClient;
using NetRatel.Mcp.Tools;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class McpReadPathContractTests
{
    [Fact]
    public async Task Active_legacy_api_backed_read_tools_mint_and_send_bearer_requests_to_their_documented_routes()
    {
        var apiRequestPaths = new List<string>();
        var client = new NetRatelAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope"),
            () => new RecordingHandler(request =>
            {
                if (request.RequestUri!.Host == "auth.example")
                {
                    return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token","expires_in":300}"""));
                }

                request.Headers.Authorization!.Scheme.Should().Be("Bearer");
                request.Headers.Authorization.Parameter.Should().Be("minted-token");
                apiRequestPaths.Add(request.RequestUri.PathAndQuery);
                return Task.FromResult(Json(HttpStatusCode.OK, "[]"));
            }));
        var store = new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), $"netratel-mcp-{Guid.NewGuid():N}.json"));
        var reads = new ReadOnlyTools(client, store);
        var mutations = new MutationTools(client);

        var results = await Task.WhenAll(
            reads.netratel_auth(),
            reads.netratel_clients("telemetry", Request("""{"clientIdentity":"client-1"}""")),
            reads.netratel_health(),
            reads.netratel_job_runs("list", Request("""{"page":1,"pageSize":1}""")),
            reads.netratel_jobs("list", Request("""{"page":1,"pageSize":1}""")),
            reads.netratel_logs(request: Request("""{"limit":1}""")),
            reads.netratel_notifications("summary"),
            reads.netratel_search("tenants", Request("""{"q":""}""")),
            reads.netratel_telemetry());

        results.Should().OnlyContain(result => result.Success);
        apiRequestPaths.Should().Contain([
            "/api/v1/auth/ai-agent/status",
            "/api/v1/clients/client-1/telemetry",
            "/health/live",
            "/health/ready",
            "/api/v1/jobruns/?page=1&pageSize=1",
            "/api/v1/jobs/?page=1&pageSize=1",
            "/api/v1/ops/ai-agent/logs?limit=1",
            "/api/v1/notifications/summary",
            "/api/v1/global-search/tenants",
            "/api/v1/telemetry/overview"
        ]);
    }

    [Fact]
    public async Task SystemTool_Rejects_RetiredSpacetimeDiagnosticOperations()
    {
        var client = new NetRatelAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope"));
        var store = new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), $"netratel-mcp-{Guid.NewGuid():N}.json"));
        var tools = new ReadOnlyTools(client, store);

        var result = await tools.netratel_system("spacetime_health");

        result.Success.Should().BeFalse();
        result.Error!.Code.Should().Be("unsupported_operation");
        result.Error.AllowedOperations.Should().Equal("version");
    }

    [Fact]
    public async Task Remote_support_v2_tool_uses_agent_keyed_gateway_routes_and_confirms_refreshes()
    {
        var requests = new List<(HttpMethod Method, string Path)>();
        var client = new NetRatelAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope"),
            () => new RecordingHandler(request =>
            {
                if (request.RequestUri!.Host == "auth.example")
                    return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token","expires_in":300}"""));

                requests.Add((request.Method, request.RequestUri.PathAndQuery));
                return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
            }));
        var tools = new MutationTools(client);
        var request = Request("""{"tenantId":9,"agentId":"c1759dad-b2f1-4950-87cd-a8e579ac99d0"}""");

        (await tools.netratel_remote_support_v2("capabilities", request)).Success.Should().BeTrue();
        (await tools.netratel_remote_support_v2("inventory", request)).Success.Should().BeTrue();
        var confirmation = await tools.netratel_remote_support_v2("refresh_inventory", request);
        confirmation.Status.Should().Be("confirmation_required");
        (await tools.netratel_remote_support_v2("refresh_inventory", request, confirm: true)).Success.Should().BeTrue();

        requests.Should().ContainInOrder(
            (HttpMethod.Get, "/api/v2/agents/9/c1759dad-b2f1-4950-87cd-a8e579ac99d0/remote-support/v2/capabilities"),
            (HttpMethod.Get, "/api/v2/agents/9/c1759dad-b2f1-4950-87cd-a8e579ac99d0/remote-support/v2/inventory"),
            (HttpMethod.Post, "/api/v2/agents/9/c1759dad-b2f1-4950-87cd-a8e579ac99d0/remote-support/v2/inventory/refresh"));
    }

    private static JsonElement Request(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string content)
        => new(statusCode) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responder(request);
    }
}

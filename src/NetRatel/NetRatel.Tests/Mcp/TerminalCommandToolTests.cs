using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NetRatel.AgentClient;
using NetRatel.Mcp.Tools;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class ConfigurationToolTests
{
    [Fact]
    public async Task Unconfirmed_safe_config_change_is_not_persisted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netratel-mcp-{Guid.NewGuid():N}.json");
        var store = new AgentClientConfigurationStore(path);
        var client = new FakeAgentClient();
        var tools = new ReadOnlyTools(client, store);

        var result = await tools.netratel_config("set", Request("""{"key":"apiBaseUrl","value":"https://new.example"}"""));

        result.Status.Should().Be("confirmation_required");
        store.Load().ApiBaseUrl.Should().BeNull();
    }

    private static JsonElement Request(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeAgentClient : INetRatelAgentClient
    {
        public AgentClientConfiguration Configuration { get; } = new("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope");
        public string? SentPath { get; private set; }
        public string? GetPath { get; private set; }
        public JsonObject? SentBody { get; private set; }
        public string? StreamSessionId { get; private set; }
        public int? StreamWaitSeconds { get; private set; }
        public int? StreamMaxEvents { get; private set; }
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("token");
        public Task<JsonArray> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new JsonArray());
        public Task<JsonNode?> GetClientsAsync(JsonObject? filters = null, CancellationToken ct = default) => Task.FromResult<JsonNode?>(new JsonArray());
        public Task<JsonNode?> GetJobsAsync(JsonObject? filters = null, CancellationToken ct = default) => Task.FromResult<JsonNode?>(new JsonArray());
        public Task<JsonNode?> GetJobRunsAsync(JsonObject? filters = null, CancellationToken ct = default) => Task.FromResult<JsonNode?>(new JsonArray());
        public Task<JsonNode?> GetAsync(string path, bool authenticated = true, CancellationToken ct = default)
        {
            GetPath = path;
            return Task.FromResult<JsonNode?>(new JsonObject { ["online"] = true, ["enabled"] = true, ["detectedOs"] = "Windows", ["availableShells"] = new JsonArray("powershell", "cmd") });
        }
        public Task<JsonNode?> GetTerminalStreamAsync(string sessionId, int waitSeconds, int maxEvents, CancellationToken ct = default)
        {
            StreamSessionId = sessionId;
            StreamWaitSeconds = waitSeconds;
            StreamMaxEvents = maxEvents;
            return Task.FromResult<JsonNode?>(new JsonObject { ["sessionId"] = sessionId, ["events"] = new JsonArray() });
        }
        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, bool authenticated = true, CancellationToken ct = default)
        {
            SentPath = path;
            SentBody = body?.DeepClone().AsObject();
            return Task.FromResult<JsonNode?>(new JsonObject { ["requestId"] = "request-123", ["clientIdentity"] = "WIN1GATE" });
        }
    }
}

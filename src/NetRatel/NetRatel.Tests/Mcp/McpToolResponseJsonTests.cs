using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NetRatel.AgentClient;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Tools;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class McpToolResponseJsonTests
{
    [Fact]
    public async Task Factory_preserves_frozen_sdk_envelopes_including_errors_and_confirmations()
    {
        var resolvers = McpJsonUtilities.DefaultOptions.TypeInfoResolverChain.ToArray();
        var converters = McpJsonUtilities.DefaultOptions.Converters.ToArray();
        var previousActivity = Activity.Current;
        var directory = Path.Combine(Path.GetTempPath(), "netratel-mcp-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Activity.Current = null;
        try
        {
            var client = new EnvelopeClient();
            var host = new NetRatelMcpHostContext(new NetRatelMcpTarget("dev", new Uri("https://api.example.test/"),
                new Uri("https://mcp.example.test/mcp"), NetRatelMcpCatalog.Revision), NetRatelMcpTransport.StreamableHttp, "Synthetic.Mcp", false);
            var configuration = AgentClientConfiguration.Empty with { ApiBaseUrl = "https://api.example.test", OidcScope = "before" };
            var store = new AgentClientConfigurationStore(Path.Combine(directory, "profile.json"));
            store.Save(configuration);
            using var services = new ServiceCollection().AddSingleton<INetRatelMcpApiClient>(client).AddSingleton(host)
                .AddSingleton<NetRatelMcpOperationalTools>()
                .AddSingleton<INetRatelAgentClient>(new NetRatelAgentClient(configuration)).AddSingleton(store).BuildServiceProvider();
            await using var server = McpServer.Create(new StreamServerTransport(new MemoryStream(), new MemoryStream(), "json-fixture"),
                new McpServerOptions { ServerInfo = new() { Name = "Synthetic.Mcp", Version = "1.0" } }, serviceProvider: services);
            var http = NetRatelMcpToolDefinitions.CreateForHttp("dev");
            Assert.Equal(27, http.Count);
            await AssertEnvelope(http, "netratel_auth", """{"operation":"status"}""",
                "c4b46cefc7963c9de227010ec90d7a002bb265af385a6836c8f16316dc0e86c8");
            client.Fail = true;
            await AssertEnvelope(http, "netratel_auth", """{"operation":"status"}""",
                "e75b40cb523aa29f9877e0b4b245ab689242c756daf749c1dbfa195b96146ff0");
            client.Fail = false;
            await AssertEnvelope(http, "netratel_auth", """{"operation":"unpublished"}""",
                "caaa1f706d59cf776c6116ecd1ad7f2ffaff4c39e631db1503a21c4b8fc51874");

            var stdio = Stdio("prod");
            Assert.Equal(33, stdio.Count);
            await AssertEnvelope(stdio, "netratel_config", """{"operation":"get","request":{"key":"oidcScope"}}""",
                "10671321a80862f5aa8ba76ed903aa75debb737fbd3545418e4467ed8af9affe");
            await AssertEnvelope(stdio, "netratel_config", """{"operation":"get","request":{"key":"secret"}}""",
                "f57461961724e3ea09034513c7b64d8851b50445118e057ca608ef67bdf0a1ae");
            await AssertEnvelope(stdio, "netratel_config", """{"operation":"set","request":{"key":"oidcScope","value":"garden Ω <tag> \"quote\""}}""",
                "2d99a34753c2adb816773946bd674a44412884c20ba70d100484e1ae0981cf7a");
            await AssertEnvelope(stdio, "netratel_config", """{"operation":"set","request":{"key":"oidcScope","value":"garden Ω <tag> \"quote\""},"confirm":true}""",
                "6e8ed09f81a206b9da7ce384656d44ae79e9911dc7d45773d8ef91485bb34d56");

            var developmentStdio = Stdio("dev");
            await AssertEnvelope(developmentStdio, "netratel_notifications", """{"operation":"mark_read","request":{"ids":["fixture-a","fixture-b"]}}""",
                "9e87d9ffd1ad301607ef82eb319a0ce8bcd308cdfe40288fad19f93e125e2885");
            await AssertEnvelope(developmentStdio, "netratel_notifications", """{"operation":"mark_read","request":{"ids":["fixture-a","fixture-b"]},"confirm":true}""",
                "e930d889494551b4a6ef8051f2bebafe737aa152fae31251060d8623f92c1a0e");
            Assert.Equal(3, client.Calls);
            Assert.Equal(resolvers, McpJsonUtilities.DefaultOptions.TypeInfoResolverChain);
            Assert.Equal(converters, McpJsonUtilities.DefaultOptions.Converters);
            Assert.True(McpJsonUtilities.DefaultOptions.IsReadOnly);

            async Task AssertEnvelope(IReadOnlyList<McpServerTool> tools, string name, string arguments, string expectedDigest)
            {
                using var json = JsonDocument.Parse(arguments);
                var request = new RequestContext<CallToolRequestParams>(server,
                    new JsonRpcRequest { Id = new RequestId("response-fixture"), Method = "tools/call" },
                    new CallToolRequestParams
                    {
                        Name = name,
                        Arguments = json.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone())
                    }) { Services = services };
                var result = await tools.Single(tool => tool.ProtocolTool.Name == name).InvokeAsync(request);
                Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
                Assert.NotNull(result.StructuredContent);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions.GetTypeInfo<CallToolResult>());
                Assert.Equal(expectedDigest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
        }
        finally
        {
            Activity.Current = previousActivity;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IReadOnlyList<McpServerTool> Stdio(string instance)
        => NetRatelMcpToolDefinitions.CreateForStdio(new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["netratel_config"] = typeof(ReadOnlyTools),
            ["netratel_remote_support_v2"] = typeof(MutationTools)
        }, instance, NetRatelMcpStdioJsonContext.Default);

    private sealed class EnvelopeClient : INetRatelMcpApiClient
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }

        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail)
                throw new NetRatelMcpApiException("forbidden", "Synthetic policy denial.", 403, false, "target_policy_denied")
                { CorrelationId = "fixture-correlation" };
            return Task.FromResult<JsonNode?>(JsonNode.Parse("""{"name":"garden Ω <tag> \"quote\"","nested":[null,true,17],"largeId":"9007199254740993"}"""));
        }

        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<JsonNode?>(JsonNode.Parse("""{"artifactId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","state":"completed"}"""));
        }
    }
}

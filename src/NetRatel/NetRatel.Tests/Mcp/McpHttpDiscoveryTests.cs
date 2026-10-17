using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NetRatel.AgentClient;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Http;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class McpHttpDiscoveryTests
{
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("synthetic-discovery-protocol-test-signing-key-2026"));

    [Fact]
    public async Task Authenticated_protocol_reports_own_release_and_exact_selected_schema_without_business_dispatch()
    {
        await using var host = await DiscoveryApplication.CreateAsync();
        using var client = host.Application.GetTestClient();
        using var initialized = await CallAsync(client, 1, "initialize", new
        {
            protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "discovery-test", version = "1.0" }
        });
        var hostIdentity = NetRatelMcpReleaseIdentity.FromAssembly(typeof(NetRatelMcpHttpApplication).Assembly);
        Assert.Equal(hostIdentity.ProductVersion, initialized.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("version").GetString());
        Assert.Equal("2025-11-25", initialized.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());

        using var listed = await CallAsync(client, 2, "tools/list", new { });
        var published = listed.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        using var status = await CallAsync(client, 3, "tools/call", new { name = "netratel_capabilities", arguments = new { operation = "status" } });
        var statusResponse = status.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.True(statusResponse.GetProperty("success").GetBoolean());
        var data = statusResponse.GetProperty("data");
        Assert.Equal("NetRatel.Mcp.Http", data.GetProperty("hostRelease").GetProperty("serviceName").GetString());
        Assert.Equal(hostIdentity.InformationalVersion, data.GetProperty("hostRelease").GetProperty("informationalVersion").GetString());
        Assert.Equal(hostIdentity.SourceRevision, data.GetProperty("hostRelease").GetProperty("sourceRevision").GetString());
        Assert.Equal("not_observed", data.GetProperty("nativeLoadedMetadata").GetString());
        Assert.Equal(published.Length, data.GetProperty("catalog").GetProperty("toolCount").GetInt32());
        Assert.Equal(published.Sum(tool => tool.GetProperty("inputSchema").GetProperty("oneOf").GetArrayLength()), data.GetProperty("catalog").GetProperty("operationCount").GetInt32());
        Assert.DoesNotContain("https://", data.GetRawText());

        // A read-scoped caller can inspect a mutation's contract without
        // acquiring that mutation's scope or dispatching its handler.
        using var schema = await CallAsync(client, 4, "tools/call", new
        {
            name = "netratel_capabilities", arguments = new { operation = "schema", request = new { tool = "netratel_policy", operation = "confirm_create" } }
        });
        var schemaResponse = schema.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.True(schemaResponse.GetProperty("success").GetBoolean());
        var expected = published.Single(tool => tool.GetProperty("name").GetString() == "netratel_policy")
            .GetProperty("inputSchema").GetProperty("oneOf").EnumerateArray()
            .Single(envelope => envelope.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == "confirm_create");
        var schemaData = schemaResponse.GetProperty("data");
        Assert.True(JsonElement.DeepEquals(expected, schemaData.GetProperty("inputSchema")));
        Assert.Equal("netratel.mcp.admin", schemaData.GetProperty("requirements").GetProperty("requiredScopeName").GetString());
        Assert.True(schemaData.GetProperty("requirements").GetProperty("requiresConfirmation").GetBoolean());
        Assert.False(schemaData.GetProperty("inspectedOperationDispatched").GetBoolean());
        Assert.InRange(Encoding.UTF8.GetByteCount(schemaResponse.GetRawText()), 1, NetRatelMcpDiscovery.MaxStructuredResponseBytes);

        using var invalid = await CallAsync(client, 5, "tools/call", new
        {
            name = "netratel_capabilities", arguments = new { operation = "schema", request = new { tool = "netratel_policy", operation = "confirm_create", confirm = true } }
        });
        Assert.Equal("validation_error", invalid.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());

        using var unauthenticated = Request(6, "tools/call", new { name = "netratel_capabilities", arguments = new { operation = "status" } });
        unauthenticated.Headers.Authorization = null;
        using var denied = await client.SendAsync(unauthenticated);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(0, host.BusinessApi.Calls);
    }

    private static HttpRequestMessage Request(int id, string method, object parameters)
    {
        var token = new JwtSecurityToken("https://auth.dev.example", "https://mcp.dev.example/mcp",
            [new Claim("scope", "mcp:read"), new Claim("groups", "netratel-operators")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id, method, @params = parameters })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return request;
    }

    private static async Task<JsonDocument> CallAsync(HttpClient client, int id, string method, object parameters)
    {
        using var request = Request(id, method, parameters);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        var data = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SingleOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal));
        return JsonDocument.Parse(data is null ? payload : data[6..]);
    }

    private sealed class DiscoveryApplication(WebApplication application, string configurationPath, RejectBusinessApi businessApi) : IAsyncDisposable
    {
        internal WebApplication Application { get; } = application;
        internal RejectBusinessApi BusinessApi { get; } = businessApi;

        internal static async Task<DiscoveryApplication> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"netratel-discovery-{Guid.NewGuid():N}.json");
            var configuration = new AgentClientConfiguration("https://api.dev.example", "https://auth.dev.example/connect/token",
                "synthetic-client", "synthetic-agent", "synthetic-not-used", "mcp:read");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(configuration, AgentClientConfiguration.JsonOptions));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.WebHost.UseTestServer();
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatel:Mcp:Http:Instance"] = "dev",
                ["NetRatel:Mcp:Http:ConfigurationPath"] = path,
                ["NetRatel:Mcp:Http:DevApiBaseUrl"] = "https://api.dev.example",
                ["NetRatel:Mcp:Http:ProdApiBaseUrl"] = "https://api.prod.example",
                ["NetRatel:Mcp:Http:PublicResourceUri"] = "https://mcp.dev.example/mcp",
                ["NetRatel:Mcp:Http:Authority"] = "https://auth.dev.example",
                ["NetRatel:Mcp:Http:Audience"] = "https://mcp.dev.example/mcp",
                ["NetRatel:Mcp:Http:RequiredScopes:0"] = "mcp:read",
                ["NetRatel:Mcp:Http:RequiredGroups:0"] = "netratel-operators",
                ["NetRatel:Mcp:Http:OperatorSurfaceEnabled"] = "false"
            });
            try
            {
                NetRatelMcpHttpApplication.ConfigureServices(builder);
                var business = new RejectBusinessApi();
                builder.Services.AddSingleton<INetRatelMcpApiClient>(business);
                builder.Services.PostConfigure<JwtBearerOptions>(NetRatelMcpHttpApplication.JwtScheme, options =>
                {
                    options.ConfigurationManager = null;
                    options.TokenValidationParameters.IssuerSigningKey = SigningKey;
                    options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                });
                var app = builder.Build();
                NetRatelMcpHttpApplication.ConfigurePipeline(app);
                await app.StartAsync();
                return new DiscoveryApplication(app, path, business);
            }
            catch
            {
                File.Delete(path);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Application.DisposeAsync();
            File.Delete(configurationPath);
        }
    }

    private sealed class RejectBusinessApi : INetRatelMcpApiClient
    {
        internal int Calls { get; private set; }
        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Discovery must not dispatch an API-backed operation.");
        }
        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Discovery must not dispatch an API-backed operation.");
        }
    }
}

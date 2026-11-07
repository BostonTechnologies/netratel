using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NetRatel.API.Endpoints.Client;
using NetRatel.AgentClient;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Http;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class ManagedOperationAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("category", "manual-integration")]
    public async Task Real_stdio_and_http_protocol_delete_preview_and_confirmation_reach_the_shared_backend(bool stdio)
    {
        await using var scenario = await Scenario.CreateAsync("netratel_flows", "delete");
        var flowId = Guid.NewGuid();
        scenario.Db.FlowDefinitions.Add(new() { Id = flowId, TenantId = 7, Name = "Synthetic protocol flow", Revision = 4,
            Enabled = true, DraftJson = "{\"schemaVersion\":2,\"protectedGraph\":true}" });
        await scenario.Db.SaveChangesAsync();
        const string credential = "nrt_ic_syntheticprotocolcredential";
        var backendBuilder = WebApplication.CreateBuilder(); backendBuilder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var backend = backendBuilder.Build();
        backend.MapPost("/api/v2/mcp/operator/application/{tool}/{operation}", async (string tool, string operation, HttpContext http) =>
        {
            if (http.Request.Headers.Authorization.ToString() != "Bearer " + credential) return Results.Unauthorized();
            // This disposable fixture supplies a canonical credential actor and current effective grants.
            // ManagedOperationAdmission still evaluates the real exact policy and the same persistence command.
            http.User = scenario.Http.User; http.RequestServices = scenario.Http.RequestServices;
            return await ManagedOperationEndpoints.InvokeAsync(tool, operation, http, scenario.Admission, http.RequestAborted);
        });
        await backend.StartAsync();
        var address = backend.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var configurationPath = Path.Combine(Path.GetTempPath(), $"netratel-flow-protocol-{Guid.NewGuid():N}.json");
        var config = new AgentClientConfiguration(address, null, null, null, null, null) { IntegrationCredential = credential };
        await File.WriteAllTextAsync(configurationPath, JsonSerializer.Serialize(config, AgentClientConfiguration.JsonOptions));
        Process? process = null; WebApplication? httpApplication = null; HttpClient? httpClient = null;
        var signing = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("synthetic-flow-protocol-signing-key-for-disposable-test"));
        try
        {
            if (stdio)
            {
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "NetRatel.sln"))) root = root.Parent;
                Assert.NotNull(root);
                var configuration = typeof(ManagedOperationAdmissionTests).Assembly.GetCustomAttributes(false)
                    .OfType<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
                var assembly = Path.Combine(root.FullName, "src", "NetRatel", "NetRatel.Mcp", "bin", configuration, "net10.0", "NetRatel.Mcp.dll");
                var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                start.ArgumentList.Add(assembly); start.Environment["NETRATEL_MCP_CONFIG"] = configurationPath;
                process = Process.Start(start)!;
                _ = process.StandardError.ReadToEndAsync();
            }
            else
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
                builder.WebHost.UseTestServer(); builder.Configuration.Sources.Clear();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> {
                    ["NetRatel:Mcp:Delegation:Enabled"] = "true", ["NetRatel:Mcp:Delegation:Issuer"] = "netratel-mcp-dev",
                    ["NetRatel:Mcp:Delegation:Audience"] = "netratel-api-dev", ["NetRatel:Mcp:Delegation:ServicePrincipal"] = "netratel-mcp-http-dev",
                    ["NetRatel:Mcp:Delegation:KeyId"] = "synthetic-flow-fixture", ["NetRatel:Mcp:Delegation:SharedKeyBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-flow-delegation-key-at-least-32-bytes")),
                    ["NetRatel:Mcp:Http:Instance"] = "dev", ["NetRatel:Mcp:Http:ConfigurationPath"] = configurationPath,
                    ["NetRatel:Mcp:Http:DevApiBaseUrl"] = "https://api.dev.example", ["NetRatel:Mcp:Http:ProdApiBaseUrl"] = "https://api.prod.example",
                    ["NetRatel:Mcp:Http:PublicResourceUri"] = "https://mcp.dev.example/mcp", ["NetRatel:Mcp:Http:Authority"] = "https://auth.dev.example",
                    ["NetRatel:Mcp:Http:Audience"] = "https://mcp.dev.example/mcp", ["NetRatel:Mcp:Http:RequiredScopes:0"] = "mcp:read",
                    ["NetRatel:Mcp:Http:RequiredGroups:0"] = "netratel-operators", ["NetRatel:Mcp:Http:OperatorSurfaceEnabled"] = "true"
                });
                // The HTTP host's outbound configuration retains a service identity; the fixture
                // adapter carries only the selected synthetic credential to this local backend.
                await File.WriteAllTextAsync(configurationPath, JsonSerializer.Serialize(new AgentClientConfiguration(
                    "https://api.dev.example", "https://auth.dev.example/token", "synthetic-service", "synthetic-user", "synthetic-password", "mcp:read"), AgentClientConfiguration.JsonOptions));
                NetRatelMcpHttpApplication.ConfigureServices(builder);
                builder.Services.AddSingleton<INetRatelMcpApiClient>(new FlowProtocolBackendClient(address, credential));
                builder.Services.PostConfigure<JwtBearerOptions>(NetRatelMcpHttpApplication.JwtScheme, options =>
                {
                    options.ConfigurationManager = null; options.TokenValidationParameters.IssuerSigningKey = signing;
                    options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                });
                httpApplication = builder.Build(); NetRatelMcpHttpApplication.ConfigurePipeline(httpApplication);
                await httpApplication.StartAsync(); httpClient = httpApplication.GetTestClient();
            }
            async Task<JsonDocument> Call(int id, string method, object parameters)
            {
                var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
                if (process is not null)
                {
                    await process.StandardInput.WriteLineAsync(json); await process.StandardInput.FlushAsync();
                    var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.NotNull(line); return JsonDocument.Parse(line);
                }
                var token = new JwtSecurityToken("https://auth.dev.example", "https://mcp.dev.example/mcp",
                    [new Claim("sub","owner-1"), new Claim("scope","mcp:read netratel.mcp.write"), new Claim("groups","netratel-operators")],
                    DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(signing, SecurityAlgorithms.HmacSha256));
                using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                request.Headers.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
                request.Headers.Accept.ParseAdd("application/json, text/event-stream");
                using var response = await httpClient!.SendAsync(request); Assert.Equal(200, (int)response.StatusCode);
                var payload = await response.Content.ReadAsStringAsync();
                var data = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).SingleOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal));
                return JsonDocument.Parse(data is null ? payload : data[6..]);
            }
            using var initialize = await Call(1,"initialize",new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "flow-protocol-fixture", version = "1.0" } });
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));
            if (process is not null) { await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"); await process.StandardInput.FlushAsync(); }
            using var tools = await Call(2,"tools/list",new { });
            var schema = tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "netratel_flows")
                .GetProperty("inputSchema").GetProperty("oneOf").EnumerateArray().Single(operation => operation.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == "delete");
            Assert.Contains("flow.delete", schema.GetRawText()); Assert.Contains("Destructive", schema.GetRawText());
            var target = new { tenantId = 7, flowId, data = new { expectedRevision = 4 } };
            using var preview = await Call(3,"tools/call",new { name = "netratel_flows", arguments = new { operation = "delete", request = target, confirm = false } });
            Assert.True(preview.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data").GetProperty("requiresConfirm").GetBoolean());
            Assert.Null(scenario.Db.FlowDefinitions.Single().DeletedAtUtc);
            using var executed = await Call(4,"tools/call",new { name = "netratel_flows", arguments = new { operation = "delete", request = target, confirm = true } });
            Assert.True(executed.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("success").GetBoolean(), executed.RootElement.GetRawText());
            var persisted = scenario.Db.FlowDefinitions.Single(); Assert.NotNull(persisted.DeletedAtUtc); Assert.False(persisted.Enabled); Assert.Equal(5, persisted.Revision);
            Assert.Equal("owner-1", scenario.Db.FlowAudits.Single().ActorId);
        }
        finally
        {
            if (process is not null) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); process.Dispose(); }
            httpClient?.Dispose(); if (httpApplication is not null) await httpApplication.DisposeAsync(); File.Delete(configurationPath);
        }
    }

    private sealed class FlowProtocolBackendClient(string address, string credential) : INetRatelMcpApiClient
    {
        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Get, path, null, cancellationToken);
        public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        {
            using var client = new HttpClient { BaseAddress = new(address) };
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var response = await client.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
}

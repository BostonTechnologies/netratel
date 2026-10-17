using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Security.Integration;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Http;
using NetRatel.Shared.Connectivity;
using NetRatel.Shared.Operations;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class ReviewedMcpAccessProtocolTests
{
    [Fact]
    public async Task Native_request_independent_approval_resume_then_actual_reads_in_two_tenants()
    {
        await using var fixture = await ReviewedMcpAccessTests.Fixture.CreateAsync();
        var key = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("reviewed-protocol-key-at-least-thirty-two-bytes"));
        var options = new McpOperatorDelegationOptions { Enabled = true, Issuer = "netratel-mcp-dev", Audience = "netratel-api-dev", ServicePrincipal = "netratel-mcp-http-dev", KeyId = "reviewed-test", SharedKeyBase64 = key };
        var apiBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        apiBuilder.WebHost.UseTestServer();
        apiBuilder.Configuration["Integration:HttpMcp:PublicResourceUri"] = ReviewedMcpAccessTests.Fixture.Resource;
        apiBuilder.Services.Configure<DeploymentBrandingOptions>(branding => branding.SiteUrl = ReviewedMcpAccessTests.Fixture.Origin);
        apiBuilder.Services.AddScoped<IDeploymentBrandingService, DeploymentBrandingService>();
        apiBuilder.Services.AddSingleton(fixture.Identity); apiBuilder.Services.AddSingleton(fixture.App); apiBuilder.Services.AddSingleton(fixture.Pairing);
        apiBuilder.Services.AddSingleton<IIntegrationCredentialService>(fixture.Credentials); apiBuilder.Services.AddSingleton<IIntegrationCredentialCurrentVerifier>(fixture.Credentials);
        apiBuilder.Services.AddSingleton<IEffectiveAccessService>(fixture.EffectiveAccess);
        apiBuilder.Services.AddSingleton<TimeProvider>(fixture.Clock);
        apiBuilder.Services.AddSingleton(options); apiBuilder.Services.AddSingleton(new McpOperatorDelegationTokenService(options));
        apiBuilder.Services.AddSingleton<IFlowDefinitionService, FlowPersistenceService>();
        apiBuilder.Services.AddSingleton(services => new ManagedOperationAdmission(fixture.EffectiveAccess, null!, fixture.Authorization, null!, fixture.App, null!, services.GetRequiredService<IHostEnvironment>()));
        apiBuilder.Services.AddAuthentication("ReviewedTest").AddScheme<AuthenticationSchemeOptions, GatewayAuthentication>("ReviewedTest", _ => { })
            .AddScheme<AuthenticationSchemeOptions, IntegrationCredentialAuthenticationHandler>(IntegrationCredentialAuthenticationHandler.SchemeName, _ => { });
        apiBuilder.Services.AddAuthorization(auth =>
        {
            auth.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser());
            auth.AddPolicy(ManagedOperationAuthorization.PolicyName, policy => policy.RequireAuthenticatedUser());
            auth.AddPolicy("McpLocalDelegationExchange", policy => policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.SchemeName).RequireAuthenticatedUser());
        });
        await using var api = apiBuilder.Build();
        api.UseAuthentication(); api.UseMiddleware<McpOperatorDelegationMiddleware>(); api.UseAuthorization();
        api.MapMcpLocalDelegationEndpoints(); api.MapMcpReviewedAccessEndpoints(); api.MapManagedOperationEndpoints();
        // The browser approval uses the existing service and exact proposal; no requester can reach this operator-only test route.
        api.MapPost("/test/operator/approve", async (IntegrationPairingApprovalRequest request, HttpContext http) =>
        {
            if (http.Request.Headers["X-Test-Operator"] != "reviewer") return Results.Forbid();
            return Results.Ok(await fixture.Pairing.ApproveAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", "reviewer")], "interactive-test")), request));
        });
        await api.StartAsync();
        var configPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new NetRatel.AgentClient.AgentClientConfiguration("https://api.example.test", "https://issuer.example.test/token", "gateway", "agent", "synthetic", "mcp:read"), NetRatel.AgentClient.AgentClientConfiguration.JsonOptions));
        try
        {
            var mcpBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            mcpBuilder.WebHost.UseTestServer();
            mcpBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatel:Mcp:Http:PublicResourceUri"] = ReviewedMcpAccessTests.Fixture.Resource,
                ["NetRatel:Mcp:Http:AllowedOrigins:0"] = "https://operator.example.test",
                ["NetRatel:Mcp:Http:Instance"] = "dev", ["NetRatel:Mcp:Http:OperatorSurfaceEnabled"] = "true",
                ["NetRatel:Mcp:Http:ConfigurationPath"] = configPath, ["NetRatel:Mcp:Http:DevApiBaseUrl"] = "https://api.example.test",
                ["NetRatel:Mcp:Http:ProdApiBaseUrl"] = "https://prod-api.example.test", ["NetRatel:Mcp:Http:LocalCredentialMode"] = "true",
                ["NetRatel:Mcp:Delegation:Enabled"] = "true", ["NetRatel:Mcp:Delegation:Issuer"] = options.Issuer,
                ["NetRatel:Mcp:Delegation:Audience"] = options.Audience, ["NetRatel:Mcp:Delegation:ServicePrincipal"] = options.ServicePrincipal,
                ["NetRatel:Mcp:Delegation:KeyId"] = options.KeyId, ["NetRatel:Mcp:Delegation:SharedKeyBase64"] = key
            });
            NetRatelMcpHttpApplication.ConfigureServices(mcpBuilder);
            mcpBuilder.Services.AddHttpClient(McpLocalCredentialAuthenticationHandler.ApiHttpClientName).ConfigurePrimaryHttpMessageHandler(() => api.GetTestServer().CreateHandler());
            mcpBuilder.Services.AddSingleton<INetRatelMcpApiClient>(services => new ApiForwarder(api.GetTestClient(), services.GetRequiredService<IMcpOperatorDelegationContext>()));
            await using var mcp = mcpBuilder.Build(); NetRatelMcpHttpApplication.ConfigurePipeline(mcp); await mcp.StartAsync();
            using var client = mcp.GetTestClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Credential.Secret);
            // Raw protected JSON must reject excess precision before DateTimeOffset can truncate it.
            foreach (var unsupportedExpiry in new[] { "2030-04-01T10:11:12.1234567Z", "2030-04-01T10:11:12.12345601Z", "2030-04-01T10:11:12.123456Z\n" })
            {
                var raw = JsonSerializer.SerializeToNode(fixture.Proposal("create", null), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                raw["expiresAtUtc"] = unsupportedExpiry;
                using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v2/mcp/local-delegation/access/start")
                { Content = new StringContent(raw.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Credential.Secret);
                message.Headers.Add("X-NetRatel-Mcp-Pairing", mcp.Services.GetRequiredService<McpLocalCredentialPairingService>().CreateAuthenticationProof());
                using var protectedClient = api.GetTestClient(); using var rejected = await protectedClient.SendAsync(message);
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            }
            Assert.Empty(await fixture.Identity.IntegrationPairingRequests.ToArrayAsync());
            var before = await CallAsync(client, "netratel_flows", "list", new { tenantId = 17 });
            Assert.False(before.GetProperty("success").GetBoolean());
            var proposal = fixture.Proposal("reconcile", fixture.Credential.CredentialId) with { ExpiresAtUtc = DateTimeOffset.Parse("2030-04-01T10:11:12.123456Z") };
            var invalidNative = JsonSerializer.SerializeToNode(proposal, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            invalidNative["expiresAtUtc"] = "2030-04-01T10:11:12.12345601Z";
            var invalidResult = await CallAsync(client, "netratel_access", "request", invalidNative);
            Assert.False(invalidResult.GetProperty("success").GetBoolean());
            Assert.Equal("validation_error", invalidResult.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("input", invalidResult.GetProperty("failure").GetProperty("layer").GetString());
            Assert.Contains("expiresAtUtc", invalidResult.GetProperty("data").GetProperty("fieldPaths").GetRawText());
            Assert.Empty(await fixture.Identity.IntegrationPairingRequests.ToArrayAsync());
            var requested = await CallAsync(client, "netratel_access", "request", proposal);
            Assert.True(requested.GetProperty("success").GetBoolean());
            var receipt = requested.GetProperty("data").GetProperty("receipt").Deserialize<ReviewedMcpAccessReceipt>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.All(receipt.EffectiveAdmission, row => Assert.False(row.PolicyAllowed));
            var malformedApproval = JsonSerializer.SerializeToNode(fixture.Approval(receipt), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            malformedApproval.AsObject().Remove("expiresAtUtc"); malformedApproval["ExpiresAtUtc"] = "2030-04-01T10:11:12.12345601Z";
            var reviewedHash = malformedApproval["reviewedProposalHash"]!.GetValue<string>();
            malformedApproval.AsObject().Remove("reviewedProposalHash"); malformedApproval["ReviewedProposalHash"] = reviewedHash;
            var malformedJson = malformedApproval.ToJsonString();
            using (var emitted = JsonDocument.Parse(malformedJson))
            {
                Assert.Equal(receipt.Request.ProposalHash, emitted.RootElement.GetProperty("ReviewedProposalHash").GetString());
                Assert.False(emitted.RootElement.TryGetProperty("reviewedProposalHash", out _));
                Assert.Equal("2030-04-01T10:11:12.12345601Z", emitted.RootElement.GetProperty("ExpiresAtUtc").GetString());
                Assert.False(emitted.RootElement.TryGetProperty("expiresAtUtc", out _));
            }
            using (var malformed = new HttpRequestMessage(HttpMethod.Post, "/test/operator/approve")
                { Content = new StringContent(malformedJson, System.Text.Encoding.UTF8, "application/json") })
            {
                malformed.Headers.Add("X-Test-Operator", "reviewer");
                using var browser = api.GetTestClient(); using var rejected = await browser.SendAsync(malformed);
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            }
            using var approval = new HttpRequestMessage(HttpMethod.Post, "/test/operator/approve") { Content = JsonContent.Create(fixture.Approval(receipt)) };
            approval.Headers.Add("X-Test-Operator", "reviewer");
            using var operatorClient = api.GetTestClient(); using var accepted = await operatorClient.SendAsync(approval); Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var resumed = await CallAsync(client, "netratel_access", "resume", new { requestId = proposal.RequestId }); Assert.True(resumed.GetProperty("success").GetBoolean());
            Assert.Equal(proposal.ExpiresAtUtc, resumed.GetProperty("data").GetProperty("receipt").GetProperty("credentialExpiresAtUtc").GetDateTimeOffset());
            foreach (var tenant in new[] { 17, 29 }) Assert.True((await CallAsync(client, "netratel_flows", "list", new { tenantId = tenant })).GetProperty("success").GetBoolean());
            Assert.False((await CallAsync(client, "netratel_flows", "list", new { tenantId = 31 })).GetProperty("success").GetBoolean());
            var ownerRole = fixture.Identity.AccessRoles.Local.Single();
            var withdrawn = ownerRole.Permissions.Single(permission => permission.Permission == NetRatelPermissions.FlowRead);
            fixture.Identity.Remove(withdrawn); ownerRole.Permissions.Remove(withdrawn); await fixture.Identity.SaveChangesAsync();
            var afterWithdrawal = await CallAsync(client, "netratel_access", "receipt", new { requestId = proposal.RequestId });
            Assert.True(afterWithdrawal.GetProperty("success").GetBoolean());
            var current = afterWithdrawal.GetProperty("data").GetProperty("receipt").Deserialize<ReviewedMcpAccessReceipt>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.All(current.EffectiveAdmission, row =>
            {
                Assert.True(row.ApplicationGrantConfigured); Assert.True(row.PolicyAllowed);
                Assert.False(row.OwnerPermissionGranted); Assert.False(row.AuthorizationAdmitted);
                Assert.Equal("owner_permission_missing", row.FailureCode);
            });
            Assert.False((await CallAsync(client, "netratel_flows", "list", new { tenantId = 17 })).GetProperty("success").GetBoolean());
        }
        finally { File.Delete(configPath); }
    }
    private static async Task<JsonElement> CallAsync(HttpClient client, string tool, string operation, object request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = new { operation, request } } }) };
        message.Headers.Accept.ParseAdd("application/json"); message.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(message); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var data = content.StartsWith("data:", StringComparison.Ordinal) || content.Contains("\ndata:", StringComparison.Ordinal) ?
            content.Split('\n').First(x => x.StartsWith("data:", StringComparison.Ordinal))[5..].Trim() : content;
        using var json = JsonDocument.Parse(data);
        var result = json.RootElement.GetProperty("result");
        return result.GetProperty("structuredContent").Clone();
    }
    private sealed class ApiForwarder(HttpClient client, IMcpOperatorDelegationContext delegation) : INetRatelMcpApiClient
    {
        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Get, path, null, cancellationToken);
        public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Add("X-Test-Gateway", "1"); request.Headers.Add(McpOperatorDelegationOptions.HeaderName, delegation.CurrentAssertion!);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new NetRatelMcpApiException("admission_denied", "The reviewed operation was denied.", (int)response.StatusCode, false);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
    private sealed class GatewayAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers.ContainsKey("X-Test-Gateway")
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("token_use", "netratel_service")], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.NoResult());
    }
}

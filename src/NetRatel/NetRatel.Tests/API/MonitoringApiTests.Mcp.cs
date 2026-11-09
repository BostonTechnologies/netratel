using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Security.Authorization;
using NetRatel.AgentClient;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Mcp;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Http;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class MonitoringApiTests
{
    [Fact]
    public async Task Real_MCP_HTTP_authoring_and_stdio_adapter_preserve_validation_and_configuration_revisions()
    {
        var fixture = new Fixture();
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase("monitoring-mcp-" + Guid.NewGuid()).Options);
        db.Tenants.Add(new Tenant { Id = 7, Name = "Disposable authoring tenant" });
        foreach (var operation in new[] { "configuration", "save_group", "save_rule", "preview_targets" })
        {
            var descriptor = ManagedOperationCatalog.Find("netratel_monitoring", operation)!;
            db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
            {
                Id = Guid.NewGuid(), Name = "Disposable authoring policy", Environment = McpOperatorEnvironment.Production,
                Effect = McpOperatorPolicyEffect.Allow, PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
                PrincipalSelectorValue = OperatorId.ToString("N"), TargetSelectorKind = McpOperatorTargetSelectorKind.Tenant,
                TenantId = 7, OperationFamily = McpOperatorOperationCatalog.Find(descriptor.Tool, operation)!.OperationFamily,
                Operation = $"netratel_monitoring/{operation}", ConstraintsJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBy = "fixture-policy-admin", Version = 1
            });
        }
        await db.SaveChangesAsync();
        var apiBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        apiBuilder.Logging.ClearProviders(); apiBuilder.WebHost.UseTestServer();
        apiBuilder.Services.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions, McpAuthoringAuth>("Fixture", _ => { });
        apiBuilder.Services.AddAuthorization(options => options.AddPolicy(ManagedOperationAuthorization.PolicyName, policy => policy.RequireAuthenticatedUser()));
        apiBuilder.Services.AddSingleton(db); apiBuilder.Services.AddSingleton<NetRatel.Infrastructure.Identity.Authorization.IEffectiveAccessService>(fixture.Access);
        apiBuilder.Services.AddSingleton(fixture.Api);
        apiBuilder.Services.AddSingleton(services => new ManagedOperationAdmission(fixture.Access, null!, new McpOperatorAuthorization(db), null!, db, null!, services.GetRequiredService<IHostEnvironment>()));
        await using var api = apiBuilder.Build(); api.UseAuthentication(); api.UseAuthorization(); api.MapManagedOperationEndpoints();
        await api.StartAsync();
        var agent = new NetRatelAgentClient(new("https://api.example.test", null, null, null, null, null) { IntegrationCredential = "nrt_ic_disposable_mcp_fixture" }, () => api.GetTestServer().CreateHandler());
        var target = new NetRatelMcpTarget("prod", new Uri("https://api.example.test"), new Uri("https://mcp.example.test/mcp"), NetRatelMcpCatalog.Revision);
        var outbound = new AuthoringOutbound(agent, target);
        var mcpBuilder = WebApplication.CreateBuilder(); mcpBuilder.Logging.ClearProviders(); mcpBuilder.WebHost.UseTestServer();
        mcpBuilder.Services.AddNetRatelMcpCore(new(target, NetRatelMcpTransport.StreamableHttp, "disposable-authoring"));
        mcpBuilder.Services.AddSingleton<INetRatelMcpApiClient>(new NetRatelMcpHttpApiClient(outbound));
        mcpBuilder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools(NetRatelMcpToolDefinitions.CreateForHttp("prod"));
        await using var mcp = mcpBuilder.Build(); mcp.MapMcp("/mcp"); await mcp.StartAsync();
        using var http = mcp.GetTestClient();
        async Task<JsonElement> Call(string operation, object request, bool confirm = false)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "netratel_monitoring", arguments = new { operation, request, confirm } } }, options: ManagedOperationCatalog.JsonOptions) };
            message.Headers.Accept.ParseAdd("application/json, text/event-stream");
            using var response = await http.SendAsync(message); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var data = body.Split('\n').SingleOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(data is null ? body : data[6..]);
            return document.RootElement.GetProperty("result").GetProperty("structuredContent").Clone();
        }
        var group = new MonitoringGroupDto(7, Guid.NewGuid(), 1, "Disposable CPU and disk group", [fixture.AgentId]);
        Assert.True((await Call("save_group", new { tenantId = 7, groupId = group.GroupId, data = new MonitoringGroupWriteDto(group, 0, "Disposable authoring acceptance") }, true)).GetProperty("success").GetBoolean());
        var targets = new MonitoringTargetSelectionDto(MonitoringTargetMode.Selected, [], [group.GroupId]);
        var stdio = new NetRatelMcpOperationalTools(new NetRatelMcpStdioApiClient(agent));
        foreach (var (metric, resource, code) in new[]
        {
            (MonitoringMetricKind.CpuUsagePercent, "", "monitoring_cpu_resource_requires_null"),
            (MonitoringMetricKind.CpuUsagePercent, "cpu", "monitoring_cpu_resource_requires_null"),
            (MonitoringMetricKind.DiskFreePercent, "", "monitoring_disk_resource_invalid"),
            (MonitoringMetricKind.DiskFreePercent, "*", "monitoring_disk_resource_invalid")
        })
        {
            var condition = new MonitoringConditionDto(metric, MonitoringNumericUnit.Percent, metric == MonitoringMetricKind.CpuUsagePercent ? 95 : 20, metric == MonitoringMetricKind.CpuUsagePercent ? 90 : 25, resource, null, []);
            var rule = fixture.Rule with { RuleId = Guid.NewGuid(), Targets = targets, Condition = condition };
            var request = new { tenantId = 7, ruleId = rule.RuleId, data = new MonitoringRuleWriteDto(rule, fixture.Config.Current.Revision, "Rejected fixture rule") };
            var revision = fixture.Config.Current.Revision;
            var rejected = await Call("save_rule", request, true);
            Assert.False(rejected.GetProperty("success").GetBoolean());
            var failure = rejected.GetProperty("failure"); Assert.Equal("input", failure.GetProperty("layer").GetString());
            Assert.Equal(code, failure.GetProperty("validation").GetProperty("code").GetString());
            Assert.Equal("data.rule.condition.resourceName", failure.GetProperty("validation").GetProperty("field").GetString());
            var parity = await stdio.netratel_monitoring("save_rule", JsonSerializer.SerializeToElement(request, ManagedOperationCatalog.JsonOptions), true);
            Assert.Equal(code, parity.Failure!.Validation!.Code); Assert.Equal("data.rule.condition.resourceName", parity.Failure.Validation.Field);
            var preview = await Call("preview_targets", new { tenantId = 7, data = new MonitoringTargetPreviewRequest(targets, condition) });
            Assert.Equal("data.condition.resourceName", preview.GetProperty("failure").GetProperty("validation").GetProperty("field").GetString());
            Assert.Equal(revision, fixture.Config.Current.Revision);
        }
        foreach (var (kind, resource, breach, recovery) in new[]
        {
            (MonitoringMetricKind.CpuUsagePercent, (string?)null, 95, 90),
            (MonitoringMetricKind.DiskFreePercent, null, 20, 25),
            (MonitoringMetricKind.DiskFreePercent, "/data", 10, 15)
        })
        {
            var condition = new MonitoringConditionDto(kind, MonitoringNumericUnit.Percent, breach, recovery, resource, null, []);
            Assert.True((await Call("preview_targets", new { tenantId = 7, data = new MonitoringTargetPreviewRequest(targets, condition) })).GetProperty("success").GetBoolean());
            var rule = fixture.Rule with { RuleId = Guid.NewGuid(), Targets = targets, Condition = condition, BreachHold = TimeSpan.FromMinutes(3) };
            Assert.True((await Call("save_rule", new { tenantId = 7, ruleId = rule.RuleId, data = new MonitoringRuleWriteDto(rule, fixture.Config.Current.Revision, "Corrected fixture rule") }, true)).GetProperty("success").GetBoolean());
        }
        var read = await Call("configuration", new { tenantId = 7 });
        Assert.Equal(3, read.GetProperty("data").GetProperty("rules").GetArrayLength());
        Assert.Equal(4, fixture.Config.Writes); Assert.Equal(4UL, fixture.Config.Current.Revision);
        var unknown = new AgentClientRemoteException("remote_request_failed", "HTTP 400", 400,
            "{\"title\":\"secret upstream exception token\",\"validation\":{\"code\":\"invented\",\"field\":\"secret\",\"remediation\":\"secret\"}}");
        Assert.Null(unknown.Validation); Assert.Null(unknown.RemoteCode);
    }

    private sealed class AuthoringOutbound(INetRatelAgentClient agent, NetRatelMcpTarget target) : INetRatelMcpOutboundClient
    {
        public NetRatelMcpTarget Target => target;
        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default) => agent.GetAsync(path, ct: cancellationToken);
        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default) => agent.SendAsync(method, path, body, ct: cancellationToken);
    }
    private sealed class McpAuthoringAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", OperatorId.ToString("N"))], "NetRatelLocal")), Scheme.Name)));
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Ops;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Telemetry;
using NetRatel.Application.Presence;
using NetRatel.API.Endpoints.Search;
using NetRatel.API.Middleware;
using NetRatel.API.Security.M2M;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorClientSearchEndpointTests(McpClientSearchPostgresFixture fixture)
    : IClassFixture<McpClientSearchPostgresFixture>
{
    private const string Authority = "https://api.dev.example";
    private const string Path = "/api/v2/mcp/operator/search/clients";
    private static readonly SymmetricSecurityKey ServiceKey = new(Encoding.UTF8.GetBytes("search-test-service-signing-key-at-least-32-bytes"));
    private static readonly McpOperatorDelegationOptions DelegationOptions = new()
    {
        Enabled = true, Issuer = "mcp-dev", Audience = "api-dev", ServicePrincipal = "mcp-service",
        KeyId = "search-test", SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("search-test-delegation-signing-key-at-least-32-bytes"))
    };

    [Fact]
    public async Task Valid_service_bearer_and_delegation_search_only_policy_visible_clients()
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        Delegate(client, "status", tool: "netratel_auth");
        (await client.GetAsync("/api/v2/mcp/operator/auth/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        Delegate(client);

        var metrics = app.Services.GetRequiredService<DatabaseCommandMetricsInterceptor>();
        metrics.Reset();
        var response = await client.GetAsync(Path + "?q=eXaMpLe");
        metrics.Snapshot().CommandCount.Should().BeLessThanOrEqualTo(3, "discovery must batch policy checks rather than query per client");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var result = JsonDocument.Parse(body);
        result.RootElement.GetProperty("items").GetArrayLength().Should().Be(10);
        result.RootElement.GetProperty("totalCount").GetInt32().Should().Be(10);
        body.Should().NotContain("denied").And.NotContain("hidden").And.NotContain("unclassified")
            .And.NotContain("disabled").And.NotContain("restricted").And.NotContain("secret-value");
        // More than 100 hidden-tenant rows exist: visibility must be applied in SQL before the limit.
        var precise = await client.GetAsync(Path + "?q=allowed-host-09");
        precise.StatusCode.Should().Be(HttpStatusCode.OK);
        (await precise.Content.ReadAsStringAsync()).Should().Contain("allowed-host-09");
        var empty = await client.GetAsync(Path + "?q=no-such-client");
        empty.StatusCode.Should().Be(HttpStatusCode.OK);
        using var emptyResult = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
        emptyResult.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);

        // The service does not gain legacy interactive Operator authorization.
        (await client.GetAsync("/api/v1/global-search/clients?q=example")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.McpOperatorAcceptedAudits.CountAsync(audit => audit.Tool == "netratel_search" && audit.Operation == "netratel_search/clients")).Should().Be(0);
    }

    [Theory]
    [InlineData("missing", "delegated_identity_required")]
    [InlineData("tampered", "delegated_identity_invalid")]
    [InlineData("resource", "delegated_identity_invalid")]
    [InlineData("instance", "delegated_identity_invalid")]
    [InlineData("tool", "delegated_identity_invalid")]
    [InlineData("operation", "delegated_identity_invalid")]
    [InlineData("target", "delegated_identity_invalid")]
    public async Task Missing_or_mismatched_signed_delegation_is_rejected(string variant, string code)
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        if (variant != "missing") Delegate(client, variant: variant);
        var response = await client.GetAsync(Path + "?q=example");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain(code).And.NotContain("allowed-host");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expired")]
    [InlineData("signature")]
    public async Task Invalid_service_authentication_cannot_use_a_valid_delegation(string variant)
    {
        using var app = await BuildAppAsync();
        using var client = Client(app, variant);
        Delegate(client);
        (await client.GetAsync(Path + "?q=example")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("scope", "operator", "example", "oauth_scope_missing")]
    [InlineData("valid", "no-policy", "example", "tenant_not_authorized")]
    [InlineData("valid", "expired-policy", "example", "tenant_not_authorized")]
    [InlineData("valid", "many-tenants", "example", "search_visibility_limit_exceeded")]
    [InlineData("valid", "operator", "denied", "target_operation_not_authorized")]
    [InlineData("valid", "operator", "restricted", "target_operation_not_authorized")]
    [InlineData("valid", "operator", "unclassified", "target_operation_not_authorized")]
    [InlineData("valid", "operator", "disabled", "target_operation_not_authorized")]
    public async Task Authenticated_denials_are_actionable_and_do_not_disclose_records(string variant, string subject, string query, string code)
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        Delegate(client, variant: variant, subject: subject);
        var response = await client.GetAsync(Path + "?q=" + query);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(code).And.Contain("netratel_search/clients").And.Contain("netratel.mcp.read")
            .And.Contain("search-correlation").And.Contain("remediation").And.NotContain("allowed-host");
    }

    [Fact]
    public async Task Broad_search_exposes_continuation_even_when_a_scan_contains_only_denied_targets()
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        Delegate(client);
        var longQuery = await client.GetAsync(Path + "?q=" + new string('x', 513));
        longQuery.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Delegate(client, subject: "broad-operator");
        var broad = await client.GetAsync(Path + "?q=hidden");
        broad.StatusCode.Should().Be(HttpStatusCode.OK);
        using var broadPage = JsonDocument.Parse(await broad.Content.ReadAsStringAsync());
        broadPage.RootElement.GetProperty("items").GetArrayLength().Should().Be(25);
        broadPage.RootElement.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        Delegate(client, subject: "broad-denied");
        var incomplete = await client.GetAsync(Path + "?q=hidden");
        incomplete.StatusCode.Should().Be(HttpStatusCode.OK);
        using var deniedPage = JsonDocument.Parse(await incomplete.Content.ReadAsStringAsync());
        deniedPage.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        deniedPage.RootElement.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        deniedPage.RootElement.GetProperty("nextOffset").GetInt32().Should().Be(100);
        var final = await client.GetAsync(Path + "?q=hidden&offset=100");
        final.StatusCode.Should().Be(HttpStatusCode.OK);
        using var finalPage = JsonDocument.Parse(await final.Content.ReadAsStringAsync());
        finalPage.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        finalPage.RootElement.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Continuation_visits_every_authorized_client_beyond_the_first_candidate_window()
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        Delegate(client, subject: "broad-operator");
        var ids = new HashSet<Guid>();
        var offset = 0;
        for (var page = 0; page < 5; page++)
        {
            var response = await client.GetAsync(Path + $"?q=hidden&limit=25&offset={offset}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var item in body.RootElement.GetProperty("items").EnumerateArray())
                ids.Add(item.GetProperty("agentId").GetGuid()).Should().BeTrue("pages must not repeat records");
            if (!body.RootElement.GetProperty("hasMore").GetBoolean()) break;
            var next = body.RootElement.GetProperty("nextOffset").GetInt32();
            next.Should().BeGreaterThan(offset);
            offset = next;
        }
        ids.Should().HaveCount(101);
    }

    [Theory]
    [InlineData("offset=-1")]
    [InlineData("offset=1000001")]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    public async Task Pagination_limits_are_validated_before_querying(string query)
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        Delegate(client);
        var response = await client.GetAsync(Path + "?" + query);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("search_pagination_invalid");
    }

    [Theory]
    [InlineData("")]
    [InlineData("eXaMpLe")]
    [InlineData("41")]
    public async Task Read_scope_discovers_only_visible_tenant_ids_and_names(string query)
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        Delegate(client, "tenants");
        var metrics = app.Services.GetRequiredService<DatabaseCommandMetricsInterceptor>();
        metrics.Reset();

        var response = await client.GetAsync("/api/v2/mcp/operator/search/tenants?q=" + query);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        metrics.Snapshot().CommandCount.Should().BeLessThanOrEqualTo(2);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        items.Should().ContainSingle();
        items[0].GetProperty("id").GetInt32().Should().Be(41);
        items[0].GetProperty("name").GetString().Should().Be("Example Organization");
        items[0].EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("id", "name");

        var hidden = await client.GetAsync("/api/v2/mcp/operator/search/tenants?q=42");
        hidden.StatusCode.Should().Be(HttpStatusCode.OK);
        using var hiddenBody = JsonDocument.Parse(await hidden.Content.ReadAsStringAsync());
        hiddenBody.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("scope", "operator", 403, "oauth_scope_missing")]
    [InlineData("valid", "no-policy", 403, "tenant_not_authorized")]
    [InlineData("valid", "expired-policy", 403, "tenant_not_authorized")]
    [InlineData("valid", "many-tenants", 403, "search_visibility_limit_exceeded")]
    [InlineData("resource", "operator", 401, "delegated_identity_invalid")]
    [InlineData("instance", "operator", 401, "delegated_identity_invalid")]
    [InlineData("target", "operator", 401, "delegated_identity_invalid")]
    public async Task Tenant_discovery_preserves_authentication_and_visibility_boundaries(
        string variant, string subject, int status, string code)
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        Delegate(client, "tenants", variant: variant, subject: subject);

        var response = await client.GetAsync("/api/v2/mcp/operator/search/tenants");

        ((int)response.StatusCode).Should().Be(status);
        (await response.Content.ReadAsStringAsync()).Should().Contain(code)
            .And.Contain("netratel_search/tenants").And.Contain("netratel.mcp.read").And.NotContain("Example Organization");
    }

    [Theory]
    [InlineData("clients", "delegated_identity_invalid")]
    [InlineData("missing", "delegated_identity_required")]
    [InlineData("tampered", "delegated_identity_invalid")]
    public async Task Tenant_discovery_rejects_missing_tampered_or_replayed_delegation(string variant, string code)
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        if (variant != "missing") Delegate(client, variant == "clients" ? "clients" : "tenants", variant: variant);

        var response = await client.GetAsync("/api/v2/mcp/operator/search/tenants");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain(code).And.NotContain("Example Organization");
    }

    [Theory]
    [InlineData("netratel_telemetry", "overview", "/telemetry/overview", "gateway-v2")]
    [InlineData("netratel_logs", "search", "/logs?limit=1", "certification-log")]
    public async Task Platform_observation_uses_real_bearer_delegation_and_full_dev_policy(
        string tool, string operation, string path, string expected)
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        Delegate(client, operation, tool, group: "netratel-mcp-dev-feature-testers", requestedScope: "netratel.mcp.observe");
        var response = await client.GetAsync("/api/v2/mcp/operator" + path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(expected);
    }

    [Theory]
    [InlineData("valid", null, "netratel.mcp.observe", HttpStatusCode.Forbidden, "target_policy_missing")]
    [InlineData("role", "netratel-mcp-dev-feature-testers", "netratel.mcp.observe", HttpStatusCode.Forbidden, "oauth_role_missing")]
    [InlineData("valid", "netratel-mcp-dev-feature-testers", "netratel.mcp.read", HttpStatusCode.Forbidden, "oauth_scope_missing")]
    [InlineData("resource", "netratel-mcp-dev-feature-testers", "netratel.mcp.observe", HttpStatusCode.Unauthorized, "delegated_identity_invalid")]
    public async Task Platform_observation_returns_actionable_auth_failures(
        string variant, string? group, string scope, HttpStatusCode status, string code)
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        Delegate(client, "search", "netratel_logs", variant, group: group, requestedScope: scope);
        var response = await client.GetAsync("/api/v2/mcp/operator/logs?limit=1");
        response.StatusCode.Should().Be(status);
        (await response.Content.ReadAsStringAsync()).Should().Contain(code).And.Contain("netratel_logs/search");
    }

    [Theory]
    [InlineData("scripts")]
    [InlineData("jobs")]
    [InlineData("requests")]
    [InlineData("tasks")]
    public async Task Full_dev_tester_can_search_each_application_directory(string operation)
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        Delegate(client, operation, group: "netratel-mcp-dev-feature-testers");
        var response = await client.GetAsync($"/api/v2/mcp/operator/search/{operation}?q=no-such-resource");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        body.RootElement.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Script_directory_preserves_large_ids_and_omits_executable_content()
    {
        using var app = await BuildAppAsync();
        using var client = Client(app);
        Delegate(client, "scripts", group: "netratel-mcp-dev-feature-testers");
        var response = await client.GetAsync("/api/v2/mcp/operator/search/scripts?q=9007199254740993");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);
        var item = body.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("id").GetString().Should().Be("9007199254740993");
        item.GetProperty("name").GetString().Should().Be("Certification directory script");
        json.Should().NotContain("executable-content-sentinel").And.NotContain("manifest-sentinel");
    }

    private async Task<IHost> BuildAppAsync()
    {
        return await Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Development).UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddLogging();
                services.AddSingleton<DatabaseCommandMetricsInterceptor>();
                services.AddSingleton(new NetRatelAkkaMigrationOptions
                {
                    Enabled = true, PresenceAuthorityEnabled = true, TelemetryShadowEnabled = true, TelemetryAuthorityEnabled = true
                });
                services.AddSingleton<IClientTelemetryRouter, TestTelemetry>();
                var logBuffer = new AiAgentOpsLogBuffer();
                logBuffer.Add(Microsoft.Extensions.Logging.LogLevel.Information, "certification", default, "certification-log", null);
                services.AddSingleton(logBuffer);
                services.AddDbContext<OrchestratorDbContext>((provider, options) => options.UseNpgsql(fixture.ConnectionString)
                    .AddInterceptors(provider.GetRequiredService<DatabaseCommandMetricsInterceptor>()));
                services.AddScoped<IMcpOperatorAuthorization, McpOperatorAuthorization>();
                services.AddScoped<IMcpOperatorSearchAuthorization, McpOperatorSearchAuthorization>();
                services.AddScoped<IMcpOperatorAccessEvaluation, McpOperatorAccessEvaluationService>();
                services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new M2MOptions { Authority = Authority, Audience = "orchestrator.api" }));
                services.AddSingleton(DelegationOptions);
                services.AddSingleton<McpOperatorDelegationTokenService>();
                services.AddSingleton<IAuthorizationHandler, AllowedClientHandler>();
                // Reproduce the legacy default choosing a scheme that does not accept the API bearer.
                services.AddAuthentication("Azure")
                    .AddJwtBearer("Azure", options => ConfigureJwt(options, "https://azure.example", "azure.api"))
                    .AddJwtBearer("M2M", options => ConfigureJwt(options, Authority, "orchestrator.api"));
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("M2MOnly", policy => policy.AddAuthenticationSchemes("M2M").RequireAuthenticatedUser()
                        .AddRequirements(new AllowedClientRequirement(["mcp-service"])));
                    options.AddPolicy("Operator", policy => policy.RequireAuthenticatedUser().RequireRole("Operator"));
                });
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseMiddleware<McpOperatorDelegationMiddleware>();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapMcpOperatorAuthenticationStatusEndpoints();
                    endpoints.MapMcpOperatorClientSearchEndpoints();
                    endpoints.MapMcpOperatorSystemObservabilityEndpoints();
                    endpoints.MapGlobalSearchEndpoints();
                });
            });
        }).StartAsync();
    }

    private static void ConfigureJwt(JwtBearerOptions options, string issuer, string audience) =>
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = issuer, ValidAudience = audience, IssuerSigningKey = ServiceKey,
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
            ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.Zero
        };

    private static HttpClient Client(IHost app, string variant = "valid")
    {
        var client = app.GetTestClient();
        if (variant == "missing") return client;
        var key = variant == "signature" ? new SymmetricSecurityKey(new byte[32]) : ServiceKey;
        var token = new JwtSecurityToken(variant == "issuer" ? "https://wrong.example" : Authority,
            variant == "audience" ? "wrong.api" : "orchestrator.api", [new Claim("client_id", "mcp-service")],
            expires: variant == "expired" ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static void Delegate(HttpClient client, string operation = "clients", string tool = "netratel_search", string variant = "valid", string subject = "operator", string? group = null, string requestedScope = "netratel.mcp.read")
    {
        client.DefaultRequestHeaders.Remove(McpOperatorDelegationOptions.HeaderName);
        var assertion = new McpOperatorDelegationTokenService(DelegationOptions).Create(
            new McpOperatorDelegationIdentity(subject, "automation-test", "automation-test", group is null ? [] : [group],
                variant == "role" ? [] : ["Observer", "Operator"], variant == "scope" ? [] : [requestedScope]),
            new McpOperatorDelegationRequest(variant == "tool" ? "netratel_logs" : tool,
                variant == "operation" ? "tenants" : operation, "search-request",
                variant == "resource" ? "https://wrong.example/mcp" : Authority + "/mcp",
                variant == "instance" ? "prod" : "dev", variant == "target" ? 41 : null, null, "search-correlation"));
        if (variant == "tampered") assertion = "invalid." + assertion;
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);
    }
    private sealed class TestTelemetry : IClientTelemetryRouter
    {
        public Task<ClientTelemetryReadModelSnapshot> GetReadModelAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ClientTelemetryReadModelSnapshot(
                [new TelemetrySnapshot(new ClientKey(41, Guid.Parse("fafbcbf6-9b2c-4dbf-8062-9890c0f50d99")),
                    1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, [], [], null, "gateway-v2", true)],
                DateTimeOffset.UtcNow));
        public Task<ClientTelemetryState> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TelemetryMessageResult> RecordAsync(RecordTelemetrySnapshot message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientTelemetryRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

}

public sealed class McpClientSearchPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(ConnectionString).Options);
        await db.Database.MigrateAsync();
        db.Scripts.Add(new ScriptDefinition
        {
            Id = 9007199254740993, Name = "Certification directory script", ScriptType = "bash",
            Content = "executable-content-sentinel", ManifestJson = "manifest-sentinel",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        db.Tenants.AddRange(new Tenant { Id = 41, Name = "Example Organization" }, new Tenant { Id = 42, Name = "Example Organization hidden tenant" });
        for (var i = 0; i < 10; i++) AddAgent(db, 41, $"allowed-{i:D2}", $"allowed-host-{i:D2}");
        var denied = AddAgent(db, 41, "denied", "denied");
        AddAgent(db, 41, "unclassified", "unclassified", profile: false);
        AddAgent(db, 41, "disabled", "disabled").Status = AgentStatus.Disabled;
        AddAgent(db, 41, "restricted", "restricted", classification: McpOperatorTargetClassification.Restricted);
        for (var i = 0; i < 101; i++) AddAgent(db, 42, $"hidden-{i:D3}", $"hidden-{i:D3}");
        db.McpOperatorPolicies.AddRange(
            Policy("operator", 41), Policy("broad-operator", 42), Policy("broad-denied", 42, effect: McpOperatorPolicyEffect.Deny),
            Policy("expired-policy", 41, expires: DateTimeOffset.UtcNow.AddMinutes(-5)),
            Policy("operator", 41, denied.Id, McpOperatorPolicyEffect.Deny));
        for (var i = 100; i < 201; i++)
        {
            db.Tenants.Add(new Tenant { Id = i, Name = $"Visibility tenant {i}" });
            db.McpOperatorPolicies.Add(Policy("many-tenants", i));
        }
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private static Agent AddAgent(OrchestratorDbContext db, int tenantId, string name, string host, bool profile = true,
        McpOperatorTargetClassification classification = McpOperatorTargetClassification.DevelopmentSafe)
    {
        var agent = new Agent { Id = Guid.NewGuid(), TenantId = tenantId, Name = name,
            DeviceInfoJson = JsonSerializer.Serialize(new { hostName = host, secret = "secret-value" }) };
        db.Agents.Add(agent);
        if (profile) db.McpOperatorTargetProfiles.Add(new McpOperatorTargetProfileRecord
        {
            TenantId = tenantId, AgentId = agent.Id, Classification = classification, TagsJson = "[]", Version = 1
        });
        return agent;
    }

    private static McpOperatorPolicyRecord Policy(string subject, int tenant, Guid? agent = null,
        McpOperatorPolicyEffect effect = McpOperatorPolicyEffect.Allow, DateTimeOffset? expires = null) => new()
    {
        Id = Guid.NewGuid(), Name = "Client discovery", Environment = McpOperatorEnvironment.Development,
        Effect = effect, PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
        PrincipalSelectorValue = subject, TenantId = tenant, AgentId = agent,
        TargetSelectorKind = agent is null ? McpOperatorTargetSelectorKind.Tenant : McpOperatorTargetSelectorKind.ExactAgent,
        TargetClassification = McpOperatorTargetClassification.DevelopmentSafe,
        OperationFamily = McpOperatorOperationFamily.Observability, Operation = "netratel_search/clients",
        ExpiresAtUtc = expires, Version = 1
    };
}

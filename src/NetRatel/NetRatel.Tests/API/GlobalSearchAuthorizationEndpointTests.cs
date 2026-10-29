using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Search;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Web.Services.Monitoring;
using System.Text.Json;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class GlobalSearchAuthorizationEndpointTests(PostgreSqlPersistenceFixture postgres)
{
    [Theory]
    [InlineData("tenants")]
    [InlineData("clients")]
    [InlineData("jobs")]
    [InlineData("requests")]
    [InlineData("tasks")]
    [InlineData("alerts")]
    [InlineData("flows")]
    [InlineData("investigations")]
    public async Task Scoped_operator_searches_only_authorized_tenant_data(string section)
    {
        using var app = await BuildAppAsync();
        await SeedAsync(app.Services, includeScopedScriptEditor: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-NetRatel-Principal", "operator-a");

        var response = await client.GetAsync($"/api/v1/global-search/{section}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("Tenant A").And.NotContain("Tenant B");
    }

    [Fact]
    public async Task Tenant_scoped_script_editor_cannot_discover_instance_scoped_script_library()
    {
        using var app = await BuildAppAsync();
        await SeedAsync(app.Services);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-NetRatel-Principal", "operator-a");

        var response = await client.GetAsync("/api/v1/global-search/scripts");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Shared script");
    }

    [Fact]
    public async Task Telemetry_grant_without_monitoring_permission_cannot_discover_alert_backed_investigations()
    {
        using var app = await BuildAppAsync(await postgres.CreateDatabaseAsync()); await SeedAsync(app.Services);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.AiInvestigations.AddRange(
                new AiInvestigationRecord { TenantId = 1, RequestId = Guid.NewGuid(), Title = "Tenant A general chat", AgentIds = [], LastActivityUtc = DateTimeOffset.UtcNow },
                new AiInvestigationRecord { TenantId = 2, RequestId = Guid.NewGuid(), Title = "Tenant A request in Tenant B", AgentIds = [], LastActivityUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("X-NetRatel-Principal", "telemetry-reader");
        var response = await client.GetAsync("/api/v1/global-search/investigations?q=Tenant%20A");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("Tenant A investigation").And.NotContain("Tenant A secret alert investigation").And.NotContain("Tenant B");
        payload.Should().Contain("Tenant A general chat").And.Contain("\"totalCount\":2");
        using var json = JsonDocument.Parse(payload);
        var chat = json.RootElement.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("title").GetString() == "Tenant A general chat");
        Assert.True(chat.GetProperty("isGeneralChat").GetBoolean());
        Assert.Equal(JsonValueKind.Null, chat.GetProperty("clientContext").ValueKind);
    }

    [Theory]
    [InlineData("alerts", "database-host")]
    [InlineData("investigations", "critical-system")]
    [InlineData("flows", "enabled")]
    public async Task Ops_sources_match_persistent_human_metadata_without_transcripts(string section, string term)
    {
        using var app = await BuildAppAsync(await postgres.CreateDatabaseAsync()); await SeedAsync(app.Services);
        var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("X-NetRatel-Principal", "operator-a");
        var response = await client.GetAsync($"/api/v1/global-search/{section}?q={term}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("Tenant A").And.NotContain("Tenant B").And.NotContain("DO-NOT-INDEX-TRANSCRIPT");
    }

    [Fact]
    public async Task Exact_alert_detail_revalidates_tenant_and_occurrence_ownership()
    {
        using var app = await BuildAppAsync(); await SeedAsync(app.Services);
        Guid id;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var row = await db.MonitoringOccurrences.SingleAsync(row => row.TenantId == 1); id = row.OccurrenceId;
            var draft = MonitoringRuleDraft.Create(); draft.Name = "Historical CPU alert";
            var rule = draft.Build(1) with { RuleId = row.RuleId };
            var evidence = new MonitoringEvidenceDto(new(1, 1), Guid.NewGuid(), row.RaisedAtUtc, row.RaisedAtUtc,
                MonitoringEvidenceQuality.Fresh, MonitoringClassification.Breach, 95, 0, null);
            row.OccurrenceJson = JsonSerializer.Serialize(new MonitoringOccurrenceDto(id, row.RaisedEventId, row.RaisedAtUtc, row.RaisedAtUtc, rule, evidence,
                MonitoringFlowDispatchDisposition.NoFlowSelected, EndedAtUtc: row.RaisedAtUtc.AddMinutes(1)));
            await db.SaveChangesAsync();
        }
        var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("X-NetRatel-Principal", "operator-a");
        var response = await client.GetAsync($"/api/v2/tenants/1/monitoring/alerts/{id:D}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("Historical CPU alert");
        (await client.GetAsync($"/api/v2/tenants/2/monitoring/alerts/{id:D}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Remove("X-NetRatel-Principal"); client.DefaultRequestHeaders.Add("X-NetRatel-Principal", "telemetry-reader");
        (await client.GetAsync($"/api/v2/tenants/1/monitoring/alerts/{id:D}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<IHost> BuildAppAsync(string? postgresConnection = null)
    {
        var builder = WebApplication.CreateBuilder();
        var applicationRoot = new InMemoryDatabaseRoot();
        var identityRoot = new InMemoryDatabaseRoot();
        var applicationDatabaseName = $"search-app-{Guid.NewGuid():N}";
        var identityDatabaseName = $"search-identity-{Guid.NewGuid():N}";
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("InteractiveAccount", policy =>
        {
            policy.AddAuthenticationSchemes(TestAuthenticationHandler.SchemeName);
            policy.RequireAuthenticatedUser();
        }));
        builder.Services.AddAuthorization(options => options.AddPolicy(NetRatel.API.Security.Authorization.MonitoringAuthorization.ReadPolicy, policy =>
        {
            policy.AddAuthenticationSchemes(TestAuthenticationHandler.SchemeName);
            policy.RequireAuthenticatedUser();
        }));
        builder.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        builder.Services.AddSingleton<DatabaseCommandMetricsInterceptor>();
        builder.Services.AddDbContext<OrchestratorDbContext>(options =>
        {
            if (postgresConnection is null) options.UseInMemoryDatabase(applicationDatabaseName, applicationRoot);
            else options.UseNpgsql(postgresConnection);
        });
        builder.Services.AddDbContext<NetRatelIdentityDbContext>(options =>
            options.UseInMemoryDatabase(identityDatabaseName, identityRoot));
        builder.Services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGlobalSearchEndpoints();
        if (postgresConnection is not null)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.MigrateAsync();
        }
        await app.StartAsync();
        return app;
    }

    private static async Task SeedAsync(IServiceProvider services, bool includeScopedScriptEditor = false)
    {
        await using var scope = services.CreateAsyncScope();
        var application = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
        var permissions = new List<string>
        {
            NetRatelPermissions.TenantAdministration,
            NetRatelPermissions.ClientManagement,
            NetRatelPermissions.JobManagement,
            NetRatelPermissions.MonitoringRead,
            NetRatelPermissions.FlowRead,
            NetRatelPermissions.TelemetryRead
        };
        if (includeScopedScriptEditor)
        {
            permissions.Add(NetRatelPermissions.ScriptEdit);
        }

        var role = new AccessRole { Name = "scoped-operator", DelegationRank = 1 };
        foreach (var permission in permissions)
        {
            role.Permissions.Add(new AccessRolePermission { Permission = permission });
        }

        identity.AccessRoles.Add(role);
        identity.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment
        {
            PrincipalId = "operator-a",
            RoleId = role.Id,
            TenantId = 1,
            Role = role
        });
        var reader = new AccessRole { Name = "telemetry-reader", DelegationRank = 1 };
        reader.Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.TelemetryRead });
        identity.AccessRoles.Add(reader);
        identity.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment { PrincipalId = "telemetry-reader", RoleId = reader.Id, TenantId = 1, Role = reader });
        var agentA = Guid.Parse("b1f0d95b-2217-4b2e-8df2-6db8bc2c9a91");
        var agentB = Guid.Parse("e9452fe3-df84-49fc-9e94-6fc98aeae215");
        var now = DateTimeOffset.Parse("2026-09-22T08:00:00+00:00");
        application.Tenants.AddRange(
            new Tenant { Id = 1, Name = "Tenant A", CreatedAtUtc = now, UpdatedAtUtc = now },
            new Tenant { Id = 2, Name = "Tenant B", CreatedAtUtc = now, UpdatedAtUtc = now });
        application.Agents.AddRange(
            new Agent { Id = agentA, TenantId = 1, Name = "Tenant A client", FriendlyName = "database-host", Tags = ["critical-system"], CreatedAtUtc = now },
            new Agent { Id = agentB, TenantId = 2, Name = "Tenant B client", CreatedAtUtc = now });
        application.Jobs.AddRange(
            new JobDefinition { Id = 1, Name = "Tenant A job", TenantId = 1, AgentId = agentA, CreatedAtUtc = now, UpdatedAtUtc = now },
            new JobDefinition { Id = 2, Name = "Tenant B job", TenantId = 2, AgentId = agentB, CreatedAtUtc = now, UpdatedAtUtc = now });
        application.Requests.AddRange(
            new RequestRecord { Id = 1, TargetTenantId = 1, TargetAgentId = agentA, TargetClientIdentity = "Tenant A client", JobDefinitionId = "1", CreatedAtUtc = now, UpdatedAtUtc = now },
            new RequestRecord { Id = 2, TargetTenantId = 2, TargetAgentId = agentB, TargetClientIdentity = "Tenant B client", JobDefinitionId = "2", CreatedAtUtc = now, UpdatedAtUtc = now });
        application.JobTaskActivities.AddRange(
            new JobTaskActivityRecord { Id = 1, TenantId = 1, AgentId = agentA, RequestId = "tenant-a", ClientIdentity = "Tenant A client", TaskType = "Run", Status = "Completed", CreatedAtUtc = now },
            new JobTaskActivityRecord { Id = 2, TenantId = 2, AgentId = agentB, RequestId = "tenant-b", ClientIdentity = "Tenant B client", TaskType = "Run", Status = "Completed", CreatedAtUtc = now });
        application.Scripts.Add(new ScriptDefinition { Id = 1, Name = "Shared script", Description = "Instance library", ScriptType = "PowerShell", CreatedAtUtc = now, UpdatedAtUtc = now });
        var occurrenceA = Guid.NewGuid(); var occurrenceB = Guid.NewGuid();
        application.MonitoringOccurrences.AddRange(
            new MonitoringOccurrenceRecord { OccurrenceId = occurrenceA, TenantId = 1, RuleId = Guid.NewGuid(), AgentId = agentA, ResourceKey = "cpu", RaisedAtUtc = now, RaisedEventId = Guid.NewGuid(), OccurrenceJson = """{"PinnedRule":{"Name":"Tenant A CPU alert","Severity":2}}""" },
            new MonitoringOccurrenceRecord { OccurrenceId = occurrenceB, TenantId = 2, RuleId = Guid.NewGuid(), AgentId = agentB, ResourceKey = "cpu", RaisedAtUtc = now, RaisedEventId = Guid.NewGuid(), OccurrenceJson = """{"PinnedRule":{"Name":"Tenant B CPU alert","Severity":2}}""" });
        application.FlowDefinitions.AddRange(
            new FlowDefinitionRecord { Id = Guid.NewGuid(), TenantId = 1, Name = "Tenant A flow", Enabled = true, DraftJson = "{}", CreatedAtUtc = now, UpdatedAtUtc = now },
            new FlowDefinitionRecord { Id = Guid.NewGuid(), TenantId = 2, Name = "Tenant B flow", Enabled = true, DraftJson = "{}", CreatedAtUtc = now, UpdatedAtUtc = now });
        var investigation = new AiInvestigationRecord { TenantId = 1, RequestId = Guid.NewGuid(), Title = "Tenant A investigation", AgentIds = [agentA], LastActivityUtc = now };
        application.AiInvestigations.AddRange(investigation,
            new AiInvestigationRecord { TenantId = 1, RequestId = Guid.NewGuid(), Title = "Tenant A secret alert investigation", AgentIds = [agentA], OccurrenceId = occurrenceA, LastActivityUtc = now },
            new AiInvestigationRecord { TenantId = 2, RequestId = Guid.NewGuid(), Title = "Tenant B investigation", AgentIds = [agentB], LastActivityUtc = now });
        application.AiInvestigationEvents.Add(new AiInvestigationEventRecord { InvestigationId = investigation.Id, Sequence = 1, Type = "assistant", Text = "DO-NOT-INDEX-TRANSCRIPT", CreatedAtUtc = now });
        await identity.SaveChangesAsync();
        await application.SaveChangesAsync();
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "GlobalSearchTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principalId = Request.Headers["X-NetRatel-Principal"].ToString();
            return string.IsNullOrWhiteSpace(principalId)
                ? Task.FromResult(AuthenticateResult.Fail("Missing test principal."))
                : Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", principalId)], SchemeName)),
                    SchemeName)));
        }
    }
}

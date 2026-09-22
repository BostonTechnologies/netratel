using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
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
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GlobalSearchAuthorizationEndpointTests
{
    [Theory]
    [InlineData("tenants")]
    [InlineData("clients")]
    [InlineData("jobs")]
    [InlineData("requests")]
    [InlineData("tasks")]
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

    private static async Task<IHost> BuildAppAsync()
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
        builder.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        builder.Services.AddSingleton<DatabaseCommandMetricsInterceptor>();
        builder.Services.AddDbContext<OrchestratorDbContext>(options =>
            options.UseInMemoryDatabase(applicationDatabaseName, applicationRoot));
        builder.Services.AddDbContext<NetRatelIdentityDbContext>(options =>
            options.UseInMemoryDatabase(identityDatabaseName, identityRoot));
        builder.Services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGlobalSearchEndpoints();
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
            NetRatelPermissions.JobManagement
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
        var agentA = Guid.Parse("b1f0d95b-2217-4b2e-8df2-6db8bc2c9a91");
        var agentB = Guid.Parse("e9452fe3-df84-49fc-9e94-6fc98aeae215");
        var now = DateTimeOffset.Parse("2026-09-22T08:00:00+00:00");
        application.Tenants.AddRange(
            new Tenant { Id = 1, Name = "Tenant A", CreatedAtUtc = now, UpdatedAtUtc = now },
            new Tenant { Id = 2, Name = "Tenant B", CreatedAtUtc = now, UpdatedAtUtc = now });
        application.Agents.AddRange(
            new Agent { Id = agentA, TenantId = 1, Name = "Tenant A client", CreatedAtUtc = now },
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

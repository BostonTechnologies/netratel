using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Security.Authorization;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AccessAdministrationEndpointTests
{
    [Fact]
    public async Task Tenant_administrator_can_assign_a_lower_rank_role_in_its_own_tenant()
    {
        using var app = await BuildAppAsync();
        await SeedAsync(app.Services);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-NetRatel-Principal", "tenant-admin");

        var response = await client.PutAsJsonAsync(
            "/api/v2/access/principals/member/assignments",
            new AccessAdministrationEndpoints.AssignRoleRequest("operator", 1));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Tenant_administrator_cannot_assign_a_role_at_its_ceiling_or_manage_another_tenant()
    {
        using var app = await BuildAppAsync();
        await SeedAsync(app.Services);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-NetRatel-Principal", "tenant-admin");

        var atCeiling = await client.PutAsJsonAsync(
            "/api/v2/access/principals/member/assignments",
            new AccessAdministrationEndpoints.AssignRoleRequest("peer-admin", 1));
        var otherTenant = await client.PutAsJsonAsync(
            "/api/v2/access/principals/member/assignments",
            new AccessAdministrationEndpoints.AssignRoleRequest("operator", 2));

        atCeiling.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        otherTenant.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Tenant_administrator_discovers_only_its_named_administration_scope()
    {
        using var app = await BuildAppAsync();
        await SeedAsync(app.Services);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-NetRatel-Principal", "tenant-admin");

        var response = await client.GetAsync("/api/v2/access/tenants");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var tenants = await response.Content.ReadFromJsonAsync<List<AccessAdministrationEndpoints.AccessTenantResponse>>();
        tenants.Should().BeEquivalentTo([new AccessAdministrationEndpoints.AccessTenantResponse(1, "Tenant A")]);
    }

    [Fact]
    public async Task Scoped_operator_discovers_only_named_credential_tenant_scopes()
    {
        using var app = await BuildAppAsync();
        await SeedAsync(app.Services);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-NetRatel-Principal", "tenant-admin");

        var response = await client.GetAsync("/api/v2/account/integration-credentials/tenant-scopes");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var tenants = await response.Content.ReadFromJsonAsync<List<IntegrationCredentialEndpoints.CredentialTenantScopeResponse>>();
        tenants.Should().BeEquivalentTo([new IntegrationCredentialEndpoints.CredentialTenantScopeResponse(1, "Tenant A")]);
    }

    [Fact]
    public async Task Credential_creation_reports_the_invalid_field_without_persisting_a_partial_credential()
    {
        using var app = await BuildAppAsync();
        await SeedAsync(app.Services);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-NetRatel-Principal", "tenant-admin");

        var validExpiry = DateTimeOffset.UtcNow.AddDays(7);
        var grant = new IntegrationCredentialGrantRequest(1, NetRatelPermissions.TelemetryRead);
        await AssertFieldAsync(new(" ", IntegrationCredentialPurpose.Api, validExpiry, [grant]), "name");
        await AssertFieldAsync(new("Invalid endpoint", IntegrationCredentialPurpose.HttpMcp, validExpiry,
            [grant], "what ?"), "resource");
        await AssertFieldAsync(new("Expired", IntegrationCredentialPurpose.Api, DateTimeOffset.UtcNow.AddMinutes(-1),
            [grant]), "expiresAtUtc");

        await using var scope = app.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>()
            .IntegrationCredentials.CountAsync()).Should().Be(0);

        async Task AssertFieldAsync(IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest request, string field)
        {
            using var response = await client.PostAsJsonAsync("/api/v2/account/integration-credentials/", request);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("errors").TryGetProperty(field, out var messages).Should().BeTrue();
            messages.GetArrayLength().Should().BePositive();
        }
    }

    private static async Task<IHost> BuildAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        var identityRoot = new InMemoryDatabaseRoot();
        var applicationRoot = new InMemoryDatabaseRoot();
        var identityDatabaseName = $"access-admin-identity-{Guid.NewGuid():N}";
        var applicationDatabaseName = $"access-admin-app-{Guid.NewGuid():N}";
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("AccessAdministration", policy =>
            {
                policy.AddAuthenticationSchemes(TestAuthenticationHandler.SchemeName);
                policy.RequireAuthenticatedUser();
            });
            options.AddPolicy("InteractiveAccount", policy =>
            {
                policy.AddAuthenticationSchemes(TestAuthenticationHandler.SchemeName);
                policy.RequireAuthenticatedUser();
            });
        });
        builder.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        builder.Services.AddDbContext<NetRatelIdentityDbContext>(options => options.UseInMemoryDatabase(identityDatabaseName, identityRoot));
        builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(applicationDatabaseName, applicationRoot));
        builder.Services.AddIdentityCore<LocalUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<NetRatelIdentityDbContext>();
        builder.Services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
        builder.Services.AddScoped<IIntegrationCredentialService, IntegrationCredentialService>();
        builder.Services.AddScoped<InstanceAdministratorInvariant>();
        builder.Services.AddScoped<IAuthorizationHandler, EffectiveAccessHandler>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAccessAdministrationEndpoints();
        app.MapIntegrationCredentialEndpoints();
        await app.StartAsync();
        return app;
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
        var app = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var tenantAdministrator = Role("tenant-admin-role", 800, NetRatelPermissions.UserRoleAdministration, NetRatelPermissions.TelemetryRead);
        var peerAdministrator = Role("peer-admin", 800, NetRatelPermissions.UserRoleAdministration, NetRatelPermissions.TelemetryRead);
        var operatorRole = Role("operator", 600, NetRatelPermissions.TelemetryRead);
        identity.ApplicationPrincipals.AddRange(
            new ApplicationPrincipal { Id = "tenant-admin" },
            new ApplicationPrincipal { Id = "member" });
        identity.AccessRoles.AddRange(tenantAdministrator, peerAdministrator, operatorRole);
        identity.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment
        {
            PrincipalId = "tenant-admin",
            RoleId = tenantAdministrator.Id,
            TenantId = 1
        });
        app.Tenants.AddRange(new Tenant { Id = 1, Name = "Tenant A" }, new Tenant { Id = 2, Name = "Tenant B" });
        await identity.SaveChangesAsync();
        await app.SaveChangesAsync();
    }

    private static AccessRole Role(string id, int delegationRank, params string[] permissions)
    {
        var role = new AccessRole { Id = id, Name = id, DelegationRank = delegationRank };
        foreach (var permission in permissions)
        {
            role.Permissions.Add(new AccessRolePermission { Permission = permission });
        }

        return role;
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "AccessAdminTest";

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

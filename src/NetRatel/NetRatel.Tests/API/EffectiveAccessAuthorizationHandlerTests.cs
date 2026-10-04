using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.API.Realtime;
using NetRatel.API.Security.Authorization;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class EffectiveAccessAuthorizationHandlerTests
{
    [Fact]
    public void Integration_credentials_cannot_use_a_route_with_only_the_default_authenticated_policy()
    {
        var integrationCredential = new ClaimsPrincipal(new ClaimsIdentity([new Claim("auth_mode", "integration_credential")], "IntegrationCredential"));
        var localAccount = new ClaimsPrincipal(new ClaimsIdentity([new Claim("auth_mode", "local")], "NetRatelLocal"));

        IntegrationCredentialAuthorizationBoundary.AllowsDefaultAuthenticatedRoute(integrationCredential).Should().BeFalse();
        IntegrationCredentialAuthorizationBoundary.AllowsDefaultAuthenticatedRoute(localAccount).Should().BeTrue();
    }

    [Fact]
    public async Task Tenant_route_authorization_does_not_cross_assignment_scope()
    {
        await using var db = CreateDb();
        var role = new AccessRole { Name = "Telemetry", DelegationRank = 1 };
        role.Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.TelemetryRead });
        db.AccessRoles.Add(role);
        db.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment { PrincipalId = "principal-a", RoleId = role.Id, TenantId = 10 });
        await db.SaveChangesAsync();
        var handler = new EffectiveAccessHandler(new EffectiveAccessService(db, Configuration()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", "principal-a")], "local"));
        var requirement = new EffectiveAccessRequirement(NetRatelPermissions.TelemetryRead);

        (await EvaluateAsync(handler, requirement, principal, 10)).Should().BeTrue();
        (await EvaluateAsync(handler, requirement, principal, 11)).Should().BeFalse();
    }

    [Fact]
    public async Task Legacy_mcp_policy_access_keeps_its_existing_scope_requirement()
    {
        await using var db = CreateDb();
        var handler = new EffectiveAccessHandler(new EffectiveAccessService(db, Configuration()));
        var requirement = new EffectiveAccessRequirement(NetRatelPermissions.McpPolicyAdministration, legacyRequiredScope: "netratel.mcp.admin");
        var withoutScope = new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles", "Operator")], "Oidc"));
        var withScope = new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles", "Operator"), new Claim("scope", "netratel.mcp.admin")], "Oidc"));

        (await EvaluateAsync(handler, requirement, withoutScope, 10)).Should().BeFalse();
        (await EvaluateAsync(handler, requirement, withScope, 10)).Should().BeTrue();
    }

    [Fact]
    public async Task Instance_artifact_administration_allows_local_admin_but_not_tenant_manager()
    {
        await using var db = CreateDb();
        db.Users.Add(new LocalUser
        {
            Id = "local-admin", PrincipalId = "admin-principal",
            IsEnabled = true, IsInstanceAdministrator = true
        });
        var role = new AccessRole { Name = "Tenant client manager", DelegationRank = 1 };
        role.Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.ClientManagement });
        db.AccessRoles.Add(role);
        db.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment
        {
            PrincipalId = "tenant-principal", RoleId = role.Id, TenantId = 10
        });
        db.IntegrationCredentials.Add(new IntegrationCredential
        {
            Id = "admin-credential",
            PublicId = "admin-credential",
            TokenPrefix = "nrt_ic_test",
            SecretHash = "hash",
            OwnerPrincipalId = "admin-principal",
            Purpose = IntegrationCredentialPurpose.Api,
            Name = "Realtime tenant grant only",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            Grants = [new IntegrationCredentialGrant
            {
                CredentialId = "admin-credential",
                TenantId = 10,
                Permission = NetRatelPermissions.ClientManagement
            }],
            InstanceGrants = [new IntegrationCredentialInstanceGrant
            {
                CredentialId = "admin-credential",
                Permission = NetRatelPermissions.ClientManagement
            }]
        });
        await db.SaveChangesAsync();
        var effectiveAccess = new EffectiveAccessService(db, Configuration());
        var handler = new EffectiveAccessHandler(effectiveAccess);
        var realtimeTenantAuthorizer = new RealtimeTenantAccessAuthorizer(effectiveAccess);
        var requirement = new EffectiveAccessRequirement(NetRatelPermissions.ClientManagement, instanceScope: true);
        var admin = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("netratel_principal_id", "admin-principal")], "local"));
        var tenantManager = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("netratel_principal_id", "tenant-principal")], "local"));
        var attenuatedAdminCredential = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("netratel_principal_id", "admin-principal"),
            new Claim("netratel_integration_credential_id", "admin-credential")], "IntegrationCredential"));

        (await EvaluateAsync(handler, requirement, admin, 10)).Should().BeTrue();
        (await EvaluateAsync(handler, requirement, tenantManager, 10)).Should().BeFalse();
        (await EvaluateAsync(handler, requirement, attenuatedAdminCredential, 10)).Should().BeFalse(
            "an integration credential stays attenuated even when its owner is an instance administrator");

        (await realtimeTenantAuthorizer.IsAuthorizedAsync(admin, 10)).Should().BeTrue();
        (await realtimeTenantAuthorizer.IsAuthorizedAsync(admin, 11)).Should().BeTrue();
        (await realtimeTenantAuthorizer.IsAuthorizedAsync(tenantManager, 10)).Should().BeTrue();
        (await realtimeTenantAuthorizer.IsAuthorizedAsync(tenantManager, 11)).Should().BeFalse();
        (await realtimeTenantAuthorizer.IsAuthorizedAsync(attenuatedAdminCredential, 10)).Should().BeTrue(
            "the credential may use its exact tenant grant if admitted by an authorized caller policy");
        (await realtimeTenantAuthorizer.IsAuthorizedAsync(attenuatedAdminCredential, 11)).Should().BeFalse();
    }

    private static async Task<bool> EvaluateAsync(EffectiveAccessHandler handler, IAuthorizationRequirement requirement, ClaimsPrincipal principal, int tenantId)
    {
        var http = new DefaultHttpContext { User = principal };
        http.Request.RouteValues["tenantId"] = tenantId;
        var context = new AuthorizationHandlerContext([requirement], principal, http);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static NetRatelIdentityDbContext CreateDb() => new(new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection().Build();
}

using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.API.Realtime.Operations;
using NetRatel.API.Security.Authorization;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class OperationsLogTenantAccessAuthorizerTests
{
    [Fact]
    public async Task LogAccessUsesEffectiveTenantScopeAndKeepsInstanceOperatorCompatibility()
    {
        await using var db = CreateDb();
        db.Users.Add(new LocalUser
        {
            Id = "local-admin",
            PrincipalId = "admin-principal",
            IsEnabled = true,
            IsInstanceAdministrator = true
        });

        var logsViewer = new AccessRole { Name = "Logs viewer", DelegationRank = 1 };
        logsViewer.Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.TelemetryRead });
        db.AccessRoles.Add(logsViewer);
        db.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment
        {
            PrincipalId = "tenant-principal",
            RoleId = logsViewer.Id,
            TenantId = 17
        });
        db.IntegrationCredentials.Add(new IntegrationCredential
        {
            Id = "admin-credential",
            PublicId = "admin-credential",
            TokenPrefix = "nrt_ic_test",
            SecretHash = "hash",
            OwnerPrincipalId = "admin-principal",
            Purpose = IntegrationCredentialPurpose.Api,
            Name = "Restricted log credential",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            Grants = [new IntegrationCredentialGrant
            {
                CredentialId = "admin-credential",
                TenantId = 17,
                Permission = NetRatelPermissions.TelemetryRead
            }],
            InstanceGrants = [new IntegrationCredentialInstanceGrant
            {
                CredentialId = "admin-credential",
                Permission = NetRatelPermissions.TelemetryRead
            }]
        });
        await db.SaveChangesAsync();

        var access = new EffectiveAccessService(db, Configuration());
        var authorizer = new OperationsLogTenantAccessAuthorizer(access);
        var localAdmin = Principal("admin-principal");
        var tenantManager = Principal("tenant-principal", ("tenant_id", "18"));
        var noRoleTenantClaim = Principal("unassigned-principal", ("tenant_id", "17"));
        var operatorPrincipal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles", "Operator")], "Oidc"));
        var attenuatedAdminCredential = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("netratel_principal_id", "admin-principal"),
            new Claim("netratel_integration_credential_id", "admin-credential")], "IntegrationCredential"));

        (await authorizer.IsAuthorizedAsync(localAdmin, 17)).Should().BeTrue();
        (await authorizer.IsAuthorizedAsync(localAdmin, 18)).Should().BeTrue();
        (await authorizer.IsAuthorizedAsync(tenantManager, 17)).Should().BeTrue();
        (await authorizer.IsAuthorizedAsync(tenantManager, 18)).Should().BeFalse(
            "an unrelated tenant_id claim cannot extend the stored tenant assignment");
        (await authorizer.IsAuthorizedAsync(noRoleTenantClaim, 17)).Should().BeFalse(
            "a tenant_id claim alone cannot grant log access");
        (await authorizer.IsAuthorizedAsync(new ClaimsPrincipal(), 17)).Should().BeFalse();
        (await authorizer.IsAuthorizedAsync(localAdmin, 0)).Should().BeFalse();
        (await authorizer.IsAuthorizedAsync(operatorPrincipal, 19)).Should().BeTrue();
        (await authorizer.IsAuthorizedAsync(attenuatedAdminCredential, 17)).Should().BeTrue(
            "the credential may use its exact telemetry grant");
        (await authorizer.IsAuthorizedAsync(attenuatedAdminCredential, 18)).Should().BeFalse();

        var handler = new EffectiveAccessHandler(access);
        var operationsHubRequirement = new EffectiveAccessRequirement(NetRatelPermissions.TelemetryRead, instanceScope: true);
        (await EvaluateAsync(handler, operationsHubRequirement, localAdmin)).Should().BeTrue();
        (await EvaluateAsync(handler, operationsHubRequirement, tenantManager)).Should().BeFalse();
        (await EvaluateAsync(handler, operationsHubRequirement, attenuatedAdminCredential)).Should().BeFalse(
            "tenant or instance grants on an integration credential do not admit it to the instance operations hub");

        typeof(OperationsHub).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Should().ContainSingle(attribute => attribute.Policy == "OperationsLogAccess");
    }

    private static async Task<bool> EvaluateAsync(
        EffectiveAccessHandler handler,
        IAuthorizationRequirement requirement,
        ClaimsPrincipal principal)
    {
        var context = new AuthorizationHandlerContext([requirement], principal, new DefaultHttpContext { User = principal });
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ClaimsPrincipal Principal(string principalId, params (string Type, string Value)[] extraClaims) =>
        new(new ClaimsIdentity(
            [new Claim("netratel_principal_id", principalId), .. extraClaims.Select(claim => new Claim(claim.Type, claim.Value))],
            "local"));

    private static NetRatelIdentityDbContext CreateDb() => new(new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection().Build();
}

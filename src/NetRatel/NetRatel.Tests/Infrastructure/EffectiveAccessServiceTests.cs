using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class EffectiveAccessServiceTests
{
    [Fact]
    public async Task Tenant_permission_and_role_scope_are_evaluated_together()
    {
        await using var db = CreateDb();
        var observer = Role("Observer", NetRatelPermissions.TelemetryRead);
        var editor = Role("ScriptEditor", NetRatelPermissions.ScriptEdit);
        db.AccessRoles.AddRange(observer, editor);
        db.PrincipalRoleAssignments.AddRange(
            new PrincipalRoleAssignment { PrincipalId = "principal-a", RoleId = observer.Id, TenantId = 1 },
            new PrincipalRoleAssignment { PrincipalId = "principal-a", RoleId = editor.Id, TenantId = 2 });
        await db.SaveChangesAsync();
        var access = new EffectiveAccessService(db, Configuration());
        var principal = Principal("principal-a");

        (await access.AuthorizeAsync(principal, NetRatelPermissions.TelemetryRead, 1)).Should().BeTrue();
        (await access.AuthorizeAsync(principal, NetRatelPermissions.ScriptEdit, 2)).Should().BeTrue();
        (await access.AuthorizeAsync(principal, NetRatelPermissions.ScriptEdit, 1)).Should().BeFalse();
        (await access.AuthorizeAsync(principal, NetRatelPermissions.TelemetryRead, 2)).Should().BeFalse();
        (await access.AuthorizeAsync(principal, NetRatelPermissions.ScriptEdit, tenantId: null)).Should().BeFalse();
    }

    [Fact]
    public async Task Legacy_operator_is_a_deliberate_compatibility_path()
    {
        await using var db = CreateDb();
        var access = new EffectiveAccessService(db, Configuration());
        var operatorPrincipal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles", "Operator")], "Oidc"));

        (await access.AuthorizeAsync(operatorPrincipal, NetRatelPermissions.RemoteSupport, 42)).Should().BeTrue();
    }

    [Fact]
    public async Task Catalog_reconciliation_never_overwrites_existing_or_custom_roles()
    {
        await using var db = CreateDb();
        var existing = Role("Observer", NetRatelPermissions.AuditRead);
        existing.IsBuiltIn = true;
        var custom = Role("CustomRead", NetRatelPermissions.TelemetryRead);
        db.AccessRoles.AddRange(existing, custom);
        await db.SaveChangesAsync();
        var access = new EffectiveAccessService(db, Configuration());

        await access.ReconcileBuiltInRolesAsync();
        await access.ReconcileBuiltInRolesAsync();

        (await db.AccessRoles.SingleAsync(role => role.Name == "Observer")).Permissions
            .Select(permission => permission.Permission).Should().ContainSingle().Which.Should().Be(NetRatelPermissions.AuditRead);
        (await db.AccessRoles.SingleAsync(role => role.Name == "CustomRead")).IsBuiltIn.Should().BeFalse();
    }

    private static NetRatelIdentityDbContext CreateDb() => new(new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static ClaimsPrincipal Principal(string principalId) => new(new ClaimsIdentity(
        [new Claim("netratel_principal_id", principalId)], "local"));

    private static AccessRole Role(string name, params string[] permissions)
    {
        var role = new AccessRole { Name = name, DelegationRank = 10 };
        foreach (var permission in permissions)
        {
            role.Permissions.Add(new AccessRolePermission { Permission = permission });
        }

        return role;
    }
}

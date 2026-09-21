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

    [Fact]
    public async Task Integration_credential_is_attenuated_even_when_its_owner_is_an_instance_administrator()
    {
        await using var db = CreateDb();
        db.Users.Add(new LocalUser { Id = "owner", UserName = "owner", PrincipalId = "principal-a", IsEnabled = true, IsInstanceAdministrator = true });
        db.IntegrationCredentials.Add(new IntegrationCredential
        {
            Id = "credential-a",
            PublicId = "credential-a",
            TokenPrefix = "nrt_ic_test",
            SecretHash = "hash",
            OwnerPrincipalId = "principal-a",
            Purpose = IntegrationCredentialPurpose.Api,
            Name = "restricted",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            Grants = [new IntegrationCredentialGrant { CredentialId = "credential-a", TenantId = 7, Permission = NetRatelPermissions.TelemetryRead }]
        });
        await db.SaveChangesAsync();
        var access = new EffectiveAccessService(db, Configuration());
        var credentialPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("netratel_principal_id", "principal-a"), new Claim("netratel_integration_credential_id", "credential-a")], "IntegrationCredential"));

        (await access.AuthorizeAsync(credentialPrincipal, NetRatelPermissions.TelemetryRead, 7)).Should().BeTrue();
        (await access.AuthorizeAsync(credentialPrincipal, NetRatelPermissions.ScriptEdit, 7)).Should().BeFalse();
        (await access.AuthorizeAsync(credentialPrincipal, NetRatelPermissions.TelemetryRead, 8)).Should().BeFalse();
        (await access.AuthorizeAsync(credentialPrincipal, NetRatelPermissions.McpDiscoveryRead, null)).Should().BeFalse();

        db.IntegrationCredentials.Single().InstanceGrants.Add(new IntegrationCredentialInstanceGrant
        {
            CredentialId = "credential-a",
            Permission = NetRatelPermissions.McpDiscoveryRead
        });
        await db.SaveChangesAsync();

        (await access.AuthorizeAsync(credentialPrincipal, NetRatelPermissions.McpDiscoveryRead, null)).Should().BeTrue();

        db.IntegrationCredentials.Single().InstanceGrants.Add(new IntegrationCredentialInstanceGrant
        {
            CredentialId = "credential-a",
            Permission = NetRatelPermissions.TenantAdministration
        });
        await db.SaveChangesAsync();

        (await access.AuthorizeAsync(credentialPrincipal, NetRatelPermissions.TenantAdministration, null)).Should().BeTrue();
    }

    [Fact]
    public async Task Credentials_issued_before_and_after_role_reduction_lose_removed_authority_immediately()
    {
        await using var db = CreateDb();
        var observer = Role("Observer", NetRatelPermissions.TelemetryRead);
        db.Users.Add(new LocalUser { Id = "owner", UserName = "owner", PrincipalId = "principal-a", IsEnabled = true });
        db.AccessRoles.Add(observer);
        db.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment { PrincipalId = "principal-a", RoleId = observer.Id, TenantId = 7 });
        db.IntegrationCredentials.AddRange(
            Credential("credential-before"),
            Credential("credential-after"));
        await db.SaveChangesAsync();

        var access = new EffectiveAccessService(db, Configuration());
        (await access.AuthorizeAsync(CredentialPrincipal("credential-before"), NetRatelPermissions.TelemetryRead, 7)).Should().BeTrue();
        (await access.AuthorizeAsync(CredentialPrincipal("credential-after"), NetRatelPermissions.TelemetryRead, 7)).Should().BeTrue();

        db.PrincipalRoleAssignments.Remove(db.PrincipalRoleAssignments.Single());
        await db.SaveChangesAsync();

        (await access.AuthorizeAsync(CredentialPrincipal("credential-before"), NetRatelPermissions.TelemetryRead, 7)).Should().BeFalse();
        (await access.AuthorizeAsync(CredentialPrincipal("credential-after"), NetRatelPermissions.TelemetryRead, 7)).Should().BeFalse();
    }

    private static NetRatelIdentityDbContext CreateDb() => new(new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static ClaimsPrincipal Principal(string principalId) => new(new ClaimsIdentity(
        [new Claim("netratel_principal_id", principalId)], "local"));

    private static ClaimsPrincipal CredentialPrincipal(string credentialId) => new(new ClaimsIdentity(
        [new Claim("netratel_principal_id", "principal-a"), new Claim("netratel_integration_credential_id", credentialId)], "IntegrationCredential"));

    private static IntegrationCredential Credential(string id) => new()
    {
        Id = id,
        PublicId = id,
        TokenPrefix = "nrt_ic_test",
        SecretHash = $"hash-{id}",
        OwnerPrincipalId = "principal-a",
        Purpose = IntegrationCredentialPurpose.Api,
        Name = id,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
        Grants = [new IntegrationCredentialGrant { CredentialId = id, TenantId = 7, Permission = NetRatelPermissions.TelemetryRead }]
    };

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

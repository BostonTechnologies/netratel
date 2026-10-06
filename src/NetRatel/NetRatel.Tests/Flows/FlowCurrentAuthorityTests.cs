using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowCurrentAuthorityTests
{
    [Fact]
    public async Task Durable_configuring_principal_must_still_exist_and_hold_execute_in_the_same_tenant()
    {
        await using var db = Database(); var id = ApplicationPrincipal.CreateId();
        db.ApplicationPrincipals.Add(new() { Id = id });
        var role = new AccessRole { Name = "Flow executor" }; role.Permissions.Add(new() { RoleId = role.Id, Permission = NetRatelPermissions.FlowExecute });
        db.AccessRoles.Add(role); db.PrincipalRoleAssignments.Add(new() { PrincipalId = id, RoleId = role.Id, TenantId = 17 }); await db.SaveChangesAsync();
        var verifier = Verifier(db); var authority = new FlowExecutionAuthorityDto(id);
        (await verifier.AuthorizeAsync(17, authority)).Should().BeTrue(); (await verifier.AuthorizeAsync(18, authority)).Should().BeFalse();
        role.Permissions.Clear(); await db.SaveChangesAsync(); (await verifier.AuthorizeAsync(17, authority)).Should().BeFalse();
        db.ApplicationPrincipals.Remove(await db.ApplicationPrincipals.SingleAsync()); await db.SaveChangesAsync();
        (await verifier.AuthorizeAsync(17, authority)).Should().BeFalse();
        (await verifier.AuthorizeAsync(17, new("Operator"))).Should().BeFalse("a display name or system identity cannot borrow administrator authority");
    }

    [Fact]
    public async Task Disabled_local_owner_cannot_execute_through_saved_administrator_or_role_authority()
    {
        await using var db = Database(); var id = ApplicationPrincipal.CreateId(); db.ApplicationPrincipals.Add(new() { Id = id });
        var user = new LocalUser { Id = "owner", UserName = "owner", PrincipalId = id, IsInstanceAdministrator = true, IsEnabled = true };
        db.Users.Add(user); await db.SaveChangesAsync(); var verifier = Verifier(db);
        (await verifier.AuthorizeAsync(17, new(id))).Should().BeTrue();
        user.IsEnabled = false; await db.SaveChangesAsync(); (await verifier.AuthorizeAsync(17, new(id))).Should().BeFalse();
    }

    [Fact]
    public async Task Saved_integration_credential_is_attenuated_and_current_revocation_expiry_and_owner_are_rechecked()
    {
        await using var db = Database(); var id = ApplicationPrincipal.CreateId(); db.ApplicationPrincipals.Add(new() { Id = id });
        db.Users.Add(new() { Id = "owner", UserName = "owner", PrincipalId = id, IsInstanceAdministrator = true, IsEnabled = true });
        var credential = new IntegrationCredential { Id = "credential", PublicId = "credential", TokenPrefix = "nrt_ic_fixture", SecretHash = "fixture-hash", OwnerPrincipalId = id,
            Purpose = IntegrationCredentialPurpose.Api, Name = "Flow fixture", ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            Grants = [new() { CredentialId = "credential", TenantId = 17, Permission = NetRatelPermissions.FlowRead }] };
        db.IntegrationCredentials.Add(credential); await db.SaveChangesAsync(); var authority = new FlowExecutionAuthorityDto(id, credential.Id); var verifier = Verifier(db);
        (await verifier.AuthorizeAsync(17, authority)).Should().BeFalse();
        credential.Grants.Add(new() { CredentialId = credential.Id, TenantId = 17, Permission = NetRatelPermissions.FlowExecute }); await db.SaveChangesAsync();
        (await verifier.AuthorizeAsync(17, authority)).Should().BeTrue(); (await verifier.AuthorizeAsync(18, authority)).Should().BeFalse();
        credential.RevokedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); (await verifier.AuthorizeAsync(17, authority)).Should().BeFalse();
        credential.RevokedAtUtc = null; credential.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync(); (await verifier.AuthorizeAsync(17, authority)).Should().BeFalse();
        credential.ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1); credential.OwnerPrincipalId = ApplicationPrincipal.CreateId(); await db.SaveChangesAsync(); (await verifier.AuthorizeAsync(17, authority)).Should().BeFalse();
    }

    private static NetRatelIdentityDbContext Database() => new(new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
    private static FlowCurrentAuthorityVerifier Verifier(NetRatelIdentityDbContext db) => new(db, new EffectiveAccessService(db, new ConfigurationBuilder().Build()));
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.RatelDesk;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class RatelDeskConnectorAuthorizationTests
{
    [Fact]
    public async Task Persisted_authority_rechecks_tenant_grants_credential_revocation_expiry_and_disabled_owner()
    {
        await using var db = new NetRatelIdentityDbContext(new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.ApplicationPrincipals.Add(new() { Id = "owner", LocalUserId = "local-owner" });
        db.Users.Add(new() { Id = "local-owner", PrincipalId = "owner", UserName = "owner", IsEnabled = true });
        var role = new AccessRole { Name = "ConnectorOwner" };
        role.Permissions.Add(new() { RoleId = role.Id, Permission = NetRatelPermissions.IntegrationManagement });
        role.Permissions.Add(new() { RoleId = role.Id, Permission = NetRatelPermissions.SecretUse });
        db.AccessRoles.Add(role); db.PrincipalRoleAssignments.Add(new() { PrincipalId = "owner", RoleId = role.Id, TenantId = 4 });
        var credential = new IntegrationCredential { Id = "credential", OwnerPrincipalId = "owner", Purpose = IntegrationCredentialPurpose.Api,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1), Grants = [new() { CredentialId = "credential", TenantId = 4, Permission = NetRatelPermissions.IntegrationManagement },
            new() { CredentialId = "credential", TenantId = 4, Permission = NetRatelPermissions.SecretUse }] };
        db.IntegrationCredentials.Add(credential); await db.SaveChangesAsync();
        var auth = new RatelDeskConnectorAuthorization(new EffectiveAccessService(db, new ConfigurationBuilder().Build()), db);
        Assert.True(await auth.CanExecuteAsync("owner", "credential", 4, CancellationToken.None));
        Assert.False(await auth.CanExecuteAsync("owner", "credential", 5, CancellationToken.None));
        credential.RevokedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        Assert.False(await auth.CanExecuteAsync("owner", "credential", 4, CancellationToken.None));
        credential.RevokedAtUtc = null; credential.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        Assert.False(await auth.CanExecuteAsync("owner", "credential", 4, CancellationToken.None));
        Assert.True(await auth.CanExecuteAsync("owner", null, 4, CancellationToken.None));
        db.Users.Single().IsEnabled = false; await db.SaveChangesAsync();
        Assert.False(await auth.CanExecuteAsync("owner", null, 4, CancellationToken.None));
        Assert.False(await auth.CanExecuteAsync("nonexistent-owner", null, 4, CancellationToken.None));
    }
}

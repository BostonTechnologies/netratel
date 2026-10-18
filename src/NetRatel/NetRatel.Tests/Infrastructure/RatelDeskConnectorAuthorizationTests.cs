using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Infrastructure.Persistence;
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
        role.Permissions.Add(new() { RoleId = role.Id, Permission = NetRatelPermissions.FlowExecute });
        role.Permissions.Add(new() { RoleId = role.Id, Permission = NetRatelPermissions.SecretUse });
        db.AccessRoles.Add(role); db.PrincipalRoleAssignments.Add(new() { PrincipalId = "owner", RoleId = role.Id, TenantId = 4 });
        await db.SaveChangesAsync();
        await using var app = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        app.Tenants.Add(new Tenant { Id = 4, Name = "Synthetic target" });
        await app.SaveChangesAsync();
        var access = new EffectiveAccessService(db, new ConfigurationBuilder().Build());
        var owner = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", "owner")], "local"));
        var request = new IntegrationCredentialCreateRequest("Delegated workflow", IntegrationCredentialPurpose.HttpMcp,
            DateTimeOffset.UtcNow.AddHours(1), [new(4, NetRatelPermissions.FlowExecute), new(4, NetRatelPermissions.SecretUse)], "https://netratel.example.test/mcp");
        var validator = new IntegrationCredentialGrantValidator(db, app, access, TimeProvider.System);
        await validator.ValidateAsync(owner, request);
        await Assert.ThrowsAsync<IntegrationCredentialGrantValidationException>(() => validator.ValidateAsync(owner,
            request with { Grants = [new(4, NetRatelPermissions.IntegrationManagement)] }));
        var created = await new IntegrationCredentialService(db).CreateAsync("owner", request);
        var credential = await db.IntegrationCredentials.SingleAsync(x => x.Id == created.CredentialId);
        var auth = new RatelDeskConnectorAuthorization(access, db);
        Assert.True(await auth.CanExecuteAsync("owner", credential.Id, 4, CancellationToken.None));
        Assert.False(await auth.CanExecuteAsync("owner", credential.Id, 5, CancellationToken.None));
        Assert.False(await auth.CanExecuteAsync("owner", "", 4, CancellationToken.None));
        Assert.False(await auth.CanExecuteAsync("owner", "missing-credential", 4, CancellationToken.None));
        var delegated = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", "owner"),
            new Claim("netratel_integration_credential_id", credential.Id)], "integration"));
        Assert.False(await auth.CanManageAsync(delegated, 4, CancellationToken.None));
        Assert.True(await auth.CanManageAsync(owner, 4, CancellationToken.None));
        credential.RevokedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        Assert.False(await auth.CanExecuteAsync("owner", credential.Id, 4, CancellationToken.None));
        credential.RevokedAtUtc = null; credential.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        Assert.False(await auth.CanExecuteAsync("owner", credential.Id, 4, CancellationToken.None));
        Assert.True(await auth.CanExecuteAsync("owner", null, 4, CancellationToken.None));
        db.Users.Single().IsEnabled = false; await db.SaveChangesAsync();
        Assert.False(await auth.CanExecuteAsync("owner", null, 4, CancellationToken.None));
        Assert.False(await auth.CanExecuteAsync("nonexistent-owner", null, 4, CancellationToken.None));
    }

    [Theory]
    [InlineData(NetRatelPermissions.FlowExecute)]
    [InlineData(NetRatelPermissions.SecretUse)]
    [InlineData(NetRatelPermissions.FlowRead)]
    public async Task Delegated_execution_requires_both_use_permissions_even_for_an_instance_administrator(string grantedPermission)
    {
        await using var db = new NetRatelIdentityDbContext(new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.ApplicationPrincipals.Add(new() { Id = "owner", LocalUserId = "local-owner" });
        db.Users.Add(new() { Id = "local-owner", PrincipalId = "owner", UserName = "owner", IsEnabled = true, IsInstanceAdministrator = true });
        await db.SaveChangesAsync();
        var issued = await new IntegrationCredentialService(db).CreateAsync("owner", new("Restricted workflow",
            IntegrationCredentialPurpose.HttpMcp, DateTimeOffset.UtcNow.AddHours(1), [new(4, grantedPermission)], "https://netratel.example.test/mcp"));
        var auth = new RatelDeskConnectorAuthorization(new EffectiveAccessService(db, new ConfigurationBuilder().Build()), db);
        Assert.False(await auth.CanExecuteAsync("owner", issued.CredentialId, 4, CancellationToken.None));
        Assert.False(await auth.CanExecuteAsync("different-owner", issued.CredentialId, 4, CancellationToken.None));
    }
}

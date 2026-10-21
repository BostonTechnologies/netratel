using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Netclaw;
using Xunit;

namespace NetRatel.Tests.Netclaw;

public sealed class NetclawAuthorityTests
{
    [Fact]
    public async Task Validated_external_operator_authority_expires_and_newer_claims_withdraw_it()
    {
        await using var db = CreateDb();
        var resolver = new ApplicationPrincipalResolver(db);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = Token(now - 60, now + 3600, "operator-group");
        var actor = (await resolver.ResolveExternalAsync(token))!;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Authorization:Oidc:AdminGroupId"] = "operator-group" }).Build();
        var authority = new NetclawAuthority(new EffectiveAccessService(db, configuration), db);
        Assert.True(await authority.CanInvokeAsync(17, actor, null, false, default));
        Assert.True(await authority.CanReadAlertAsync(17, actor, null, default));
        configuration["Authorization:Oidc:AdminGroupId"] = "replacement-group";
        Assert.False(await authority.CanInvokeAsync(17, actor, null, false, default));
        configuration["Authorization:Oidc:AdminGroupId"] = "operator-group";
        await resolver.ResolveExternalAsync(Token(now - 30, now + 3600, "ordinary-group"));
        Assert.False(await authority.CanInvokeAsync(17, actor, null, false, default));
        await resolver.ResolveExternalAsync(token); // An older still-valid token cannot restore withdrawn authority.
        Assert.False(await authority.CanInvokeAsync(17, actor, null, false, default));
        await resolver.ResolveExternalAsync(Token(now - 10, now - 1, "operator-group"));
        Assert.False(await authority.CanInvokeAsync(17, actor, null, false, default));
    }

    [Fact]
    public async Task External_owner_credential_remains_attenuated_to_current_tenant_grants_and_revocation()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var actor = (await new ApplicationPrincipalResolver(db).ResolveExternalAsync(Token(now - 60, now + 3600, "Operator")))!;
        var credential = new IntegrationCredential { Id = "bounded", OwnerPrincipalId = actor,
            Purpose = IntegrationCredentialPurpose.Api, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };
        foreach (var permission in new[] { NetRatelPermissions.ClientManagement, NetRatelPermissions.TelemetryRead, NetRatelPermissions.SecretUse })
            credential.Grants.Add(new() { TenantId = 17, Permission = permission });
        db.IntegrationCredentials.Add(credential); await db.SaveChangesAsync();
        var authority = new NetclawAuthority(new EffectiveAccessService(db, new ConfigurationBuilder().Build()), db);
        Assert.True(await authority.CanInvokeAsync(17, actor, credential.Id, false, default));
        Assert.False(await authority.CanInvokeAsync(18, actor, credential.Id, false, default));
        credential.RevokedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        Assert.False(await authority.CanInvokeAsync(17, actor, credential.Id, false, default));
        Assert.False(await authority.CanInvokeAsync(17, "another-owner", credential.Id, false, default));
    }

    [Fact]
    public async Task Forwarded_roles_do_not_become_saved_authority_and_local_tenant_roles_stay_scoped()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var untrusted = Token(now - 60, now + 3600, "Operator", "Forwarded");
        var actor = (await new ApplicationPrincipalResolver(db).ResolveExternalAsync(untrusted))!;
        var role = new AccessRole { Name = "TenantResponder" };
        foreach (var permission in new[] { NetRatelPermissions.ClientManagement, NetRatelPermissions.TelemetryRead, NetRatelPermissions.SecretUse })
            role.Permissions.Add(new() { Permission = permission });
        db.AccessRoles.Add(role);
        db.PrincipalRoleAssignments.Add(new() { PrincipalId = actor, RoleId = role.Id, TenantId = 17 });
        await db.SaveChangesAsync();
        var authority = new NetclawAuthority(new EffectiveAccessService(db, new ConfigurationBuilder().Build()), db);
        Assert.True(await authority.CanInvokeAsync(17, actor, null, false, default));
        Assert.False(await authority.CanInvokeAsync(18, actor, null, false, default));
        db.PrincipalRoleAssignments.Remove(await db.PrincipalRoleAssignments.SingleAsync()); await db.SaveChangesAsync();
        Assert.False(await authority.CanInvokeAsync(17, actor, null, false, default));
    }

    private static NetRatelIdentityDbContext CreateDb() => new(new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static ClaimsPrincipal Token(long issued, long expires, string group, string scheme = "Oidc") => new(new ClaimsIdentity([
        new("iss", "https://issuer.example.test"), new("sub", "external-operator"), new("groups", group),
        new("iat", issued.ToString(CultureInfo.InvariantCulture)), new("exp", expires.ToString(CultureInfo.InvariantCulture))], scheme));
}

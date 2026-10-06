using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;

/// <summary>
/// Creates isolated durable test authority through production services. It never
/// replaces Program authentication, authorization, or the current evaluator.
/// Introduced with monitoring and reused by later account-permission features.
/// </summary>
internal sealed class CurrentPermissionApiFixture : IAsyncDisposable
{
    private readonly ApiFactory factory;
    private readonly string secret;
    // This fixture owns the host; callers must not dispose Host separately.
    public ApiFactory Host => factory;
    public int TenantId { get; }
    public int ForeignTenantId { get; }
    public string PrincipalId { get; }
    public string RoleId { get; }
    public string CredentialId { get; }
    public HttpClient LocalAccount { get; }

    private CurrentPermissionApiFixture(ApiFactory factory, int tenantId, int foreignTenantId,
        string principalId, string roleId, IntegrationCredentialSecret credential, HttpClient localAccount)
    {
        this.factory = factory; TenantId = tenantId; ForeignTenantId = foreignTenantId;
        PrincipalId = principalId; RoleId = roleId; CredentialId = credential.CredentialId;
        secret = credential.Secret; LocalAccount = localAccount;
    }

    public static async Task<CurrentPermissionApiFixture> CreateAsync(ApiFactory factory,
        IReadOnlyList<string> rolePermissions, IReadOnlyList<string> credentialPermissions,
        bool includeForeignCredentialGrants = false, CancellationToken ct = default)
    {
        var suffix = Guid.NewGuid().ToString("N"); var email = $"permission-{suffix}@example.test";
        // A real production host per independent case retains the exact login
        // limiter while preventing unrelated tests from consuming its IP budget.
        // The sibling shares the existing PostgreSQL container and auth settings;
        // its only override is the required unique actor-system name.
        var host = factory.CreateRuntimeSibling(new Dictionary<string, string?>
        { ["NetRatelAkka:ActorSystemName"] = "PermissionFixture" + suffix });
        HttpClient? local = null;
        try
        {
            local = await host.CreateLocalUserClientAsync(email, "A1! permission test passphrase");
            await using var scope = host.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var owner = await identity.Users.AsNoTracking().SingleAsync(user => user.Email == email, ct);
            var tenant = new Tenant { Name = "Permission owned " + suffix };
            var foreign = new Tenant { Name = "Permission foreign " + suffix };
            db.Tenants.AddRange(tenant, foreign); await db.SaveChangesAsync(ct);
            var role = new AccessRole { Name = "Permission test " + suffix };
            foreach (var permission in rolePermissions.Distinct(StringComparer.Ordinal))
                role.Permissions.Add(new AccessRolePermission { RoleId = role.Id, Permission = permission });
            identity.AccessRoles.Add(role);
            identity.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment
            { PrincipalId = owner.PrincipalId, RoleId = role.Id, TenantId = tenant.Id });
            await identity.SaveChangesAsync(ct);
            var grants = credentialPermissions.Distinct(StringComparer.Ordinal)
                .Select(permission => new IntegrationCredentialGrantRequest(tenant.Id, permission)).ToList();
            if (includeForeignCredentialGrants)
                grants.AddRange(credentialPermissions.Distinct(StringComparer.Ordinal)
                    .Select(permission => new IntegrationCredentialGrantRequest(foreign.Id, permission)));
            var credentials = scope.ServiceProvider.GetRequiredService<IIntegrationCredentialService>();
            var credential = await credentials.CreateAsync(owner.PrincipalId,
                new("Permission test", IntegrationCredentialPurpose.Api, DateTimeOffset.UtcNow.AddHours(1), grants), ct);
            return new(host, tenant.Id, foreign.Id, owner.PrincipalId, role.Id, credential, local);
        }
        catch
        {
            local?.Dispose();
            await ((IAsyncDisposable)host).DisposeAsync();
            throw;
        }
    }

    public HttpClient CredentialClient(ApiFactory? target = null)
    {
        var client = (target ?? factory).CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    public ApiFactory CreateReplica() => factory.CreateRuntimeSibling(new Dictionary<string, string?>
    { ["NetRatelAkka:ActorSystemName"] = "PermissionReplica" + Guid.NewGuid().ToString("N") });

    public async Task ChangeCurrentAuthorityAsync(string change, string permission, CancellationToken ct = default)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
        switch (change)
        {
            case "revoked":
                if (!await scope.ServiceProvider.GetRequiredService<IIntegrationCredentialService>()
                    .RevokeAsync(PrincipalId, CredentialId, PrincipalId, ct))
                    throw new InvalidOperationException("The isolated test credential was not found.");
                return;
            case "expired":
                (await identity.IntegrationCredentials.SingleAsync(row => row.Id == CredentialId, ct))
                    .ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                break;
            case "disabled":
                var owner = await identity.Users.SingleAsync(row => row.PrincipalId == PrincipalId, ct);
                owner.IsEnabled = false; owner.AuthorizationRevision++;
                break;
            case "grant":
                identity.IntegrationCredentialGrants.RemoveRange(await identity.IntegrationCredentialGrants
                    .Where(row => row.CredentialId == CredentialId && row.Permission == permission).ToListAsync(ct));
                break;
            case "role":
                identity.AccessRolePermissions.RemoveRange(await identity.AccessRolePermissions
                    .Where(row => row.RoleId == RoleId && row.Permission == permission).ToListAsync(ct));
                break;
            default: throw new ArgumentException("Unknown current authority change.", nameof(change));
        }
        await identity.SaveChangesAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        LocalAccount.Dispose();
        await ((IAsyncDisposable)factory).DisposeAsync();
    }
}

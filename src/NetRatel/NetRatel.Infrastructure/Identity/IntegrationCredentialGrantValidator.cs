using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Infrastructure.Identity;

public interface IIntegrationCredentialGrantValidator
{
    Task ValidateAsync(ClaimsPrincipal principal, IntegrationCredentialCreateRequest request, CancellationToken cancellationToken = default);
}

public sealed class IntegrationCredentialGrantValidationException(string field, bool forbidden = false)
    : Exception("Review the requested connection and currently assignable access.")
{
    public string Field { get; } = field;
    public bool Forbidden { get; } = forbidden;
    public IntegrationPairingFailure? ReviewFailure { get; init; }
}

/// <summary>Shared account-create and pairing approval/redemption authority checks.</summary>
public sealed class IntegrationCredentialGrantValidator(NetRatelIdentityDbContext identityDb,
    OrchestratorDbContext applicationDb, IEffectiveAccessService access, TimeProvider clock) : IIntegrationCredentialGrantValidator
{
    public async Task ValidateAsync(ClaimsPrincipal principal, IntegrationCredentialCreateRequest request, CancellationToken cancellationToken = default)
    {
        var owner = principal.FindFirstValue("netratel_principal_id");
        if (string.IsNullOrWhiteSpace(owner)) throw new IntegrationCredentialGrantValidationException("owner", true);
        var local = await identityDb.Users.AsNoTracking().Where(user => user.PrincipalId == owner)
            .Select(user => new { user.IsEnabled }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (local is { IsEnabled: false } || (local is null && !await identityDb.ApplicationPrincipals.AsNoTracking()
                .AnyAsync(principal => principal.Id == owner, cancellationToken).ConfigureAwait(false)))
            throw new IntegrationCredentialGrantValidationException("owner", true);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128 || request.Name.Any(char.IsControl))
            throw new IntegrationCredentialGrantValidationException("name");
        if (!Enum.IsDefined(request.Purpose)) throw new IntegrationCredentialGrantValidationException("purpose");
        var now = clock.GetUtcNow();
        if (request.ExpiresAtUtc <= now || request.ExpiresAtUtc > now.AddDays(365))
            throw new IntegrationCredentialGrantValidationException("expiresAtUtc");
        if ((request.Purpose == IntegrationCredentialPurpose.HttpMcp && !McpResourceUri.TryNormalize(request.Resource, out _)) ||
            (request.Purpose == IntegrationCredentialPurpose.Api && !string.IsNullOrWhiteSpace(request.Resource)))
            throw new IntegrationCredentialGrantValidationException("resource");
        var grants = request.Grants ?? [];
        var instance = request.InstancePermissions ?? [];
        if ((grants.Count == 0 && instance.Count == 0) || grants.Count > 64 || grants.Distinct().Count() != grants.Count ||
            grants.Any(grant => grant.TenantId <= 0 || !IsDelegable(grant.Permission)))
            throw new IntegrationCredentialGrantValidationException("grants");
        if (instance.Count > 32 || instance.Distinct(StringComparer.Ordinal).Count() != instance.Count || instance.Any(permission => !IsDelegable(permission)))
            throw new IntegrationCredentialGrantValidationException("instancePermissions");
        var tenantIds = grants.Select(grant => grant.TenantId).Distinct().ToArray();
        if (await applicationDb.Tenants.AsNoTracking().CountAsync(tenant => tenantIds.Contains(tenant.Id), cancellationToken).ConfigureAwait(false) != tenantIds.Length)
            throw new IntegrationCredentialGrantValidationException("grants");
        foreach (var grant in grants)
            if (!await access.AuthorizeAsync(principal, grant.Permission, grant.TenantId, cancellationToken).ConfigureAwait(false))
                throw new IntegrationCredentialGrantValidationException("grants", true);
        foreach (var permission in instance)
            if (!await access.AuthorizeAsync(principal, permission, null, cancellationToken).ConfigureAwait(false))
                throw new IntegrationCredentialGrantValidationException("instancePermissions", true);
    }

    private static bool IsDelegable(string permission) => !string.IsNullOrWhiteSpace(permission) &&
        string.Equals(permission, permission.Trim(), StringComparison.Ordinal) &&
        NetRatelPermissions.All.Contains(permission) && permission != NetRatelPermissions.IntegrationManagement;
}

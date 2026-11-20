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

    internal static IntegrationCredentialGrantValidationException Invalid(string field, string code, int? tenantId = null,
        int? requested = null, int? limit = null, bool forbidden = false) => new(field, forbidden)
    {
        ReviewFailure = IntegrationPairingFailure.ForCode(code, "validation") with
        { Field = field, TenantId = tenantId, RequestedCount = requested, Limit = limit }
    };
}

/// <summary>Shared account-create and pairing approval/redemption authority checks.</summary>
public sealed class IntegrationCredentialGrantValidator(NetRatelIdentityDbContext identityDb,
    OrchestratorDbContext applicationDb, IEffectiveAccessService access, TimeProvider clock) : IIntegrationCredentialGrantValidator
{
    public async Task ValidateAsync(ClaimsPrincipal principal, IntegrationCredentialCreateRequest request, CancellationToken cancellationToken = default)
    {
        var owner = principal.FindFirstValue("netratel_principal_id");
        if (string.IsNullOrWhiteSpace(owner))
            throw IntegrationCredentialGrantValidationException.Invalid("owner", "review_owner_authority_missing", forbidden: true);
        var local = await identityDb.Users.AsNoTracking().Where(user => user.PrincipalId == owner)
            .Select(user => new { user.IsEnabled }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (local is { IsEnabled: false } || (local is null && !await identityDb.ApplicationPrincipals.AsNoTracking()
                .AnyAsync(principal => principal.Id == owner, cancellationToken).ConfigureAwait(false)))
            throw IntegrationCredentialGrantValidationException.Invalid("owner", "review_owner_authority_missing", forbidden: true);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128 || request.Name.Any(char.IsControl))
            throw IntegrationCredentialGrantValidationException.Invalid("name", "integration_name_invalid");
        if (!Enum.IsDefined(request.Purpose))
            throw IntegrationCredentialGrantValidationException.Invalid("purpose", "integration_purpose_invalid");
        var now = clock.GetUtcNow();
        if (request.ExpiresAtUtc <= now || request.ExpiresAtUtc > now.AddDays(365))
            throw IntegrationCredentialGrantValidationException.Invalid("expiresAtUtc", "integration_expiry_invalid");
        if ((request.Purpose == IntegrationCredentialPurpose.HttpMcp && !McpResourceUri.TryNormalize(request.Resource, out _)) ||
            (request.Purpose == IntegrationCredentialPurpose.Api && !string.IsNullOrWhiteSpace(request.Resource)))
            throw IntegrationCredentialGrantValidationException.Invalid("resource", "integration_resource_invalid");
        var grants = request.Grants ?? [];
        var instance = request.InstancePermissions ?? [];
        ValidateGrantShape(grants, instance, request.CurrentTenantIds);
        var scopes = grants.GroupBy(grant => grant.TenantId).ToArray();
        var tenantIds = scopes.Select(scope => scope.Key).ToArray();
        if (tenantIds.Length > 0 &&
            await applicationDb.Tenants.AsNoTracking().CountAsync(tenant => tenantIds.Contains(tenant.Id), cancellationToken).ConfigureAwait(false) != tenantIds.Length)
            throw IntegrationCredentialGrantValidationException.Invalid("grants", "integration_tenant_unavailable");
        // One snapshot per scope retains the permission/tenant intersection without a
        // database round trip for every selected permission.
        foreach (var scope in scopes)
            await RequirePermissionsAsync(scope.Select(grant => grant.Permission), scope.Key, "grants").ConfigureAwait(false);
        if (instance.Count > 0)
            await RequirePermissionsAsync(instance, null, "instancePermissions").ConfigureAwait(false);

        async Task RequirePermissionsAsync(IEnumerable<string> permissions, int? tenantId, string field)
        {
            var snapshot = await access.GetSnapshotAsync(principal, tenantId, cancellationToken).ConfigureAwait(false);
            if (!snapshot.IsLegacyOperator && !snapshot.IsInstanceAdministrator && !permissions.All(snapshot.Permissions.Contains))
                throw IntegrationCredentialGrantValidationException.Invalid(field, "review_owner_authority_missing", forbidden: true);
        }
    }

    internal static void ValidateGrantCounts(int tenantGrants, int instancePermissions)
    {
        var limits = IntegrationPermissionCatalog.Limits;
        if (tenantGrants > limits.TenantGrants)
            throw IntegrationCredentialGrantValidationException.Invalid("grants", "integration_grant_capacity", requested: tenantGrants, limit: limits.TenantGrants);
        if (instancePermissions > limits.PermissionsPerScope)
            throw IntegrationCredentialGrantValidationException.Invalid("instancePermissions", "integration_scope_capacity",
                requested: instancePermissions, limit: limits.PermissionsPerScope);
    }

    internal static void ValidateGrantShape(IReadOnlyList<IntegrationCredentialGrantRequest> grants, IReadOnlyList<string> instance,
        IReadOnlyCollection<int>? currentTenantIds = null)
    {
        ValidateGrantCounts(grants.Count, instance.Count);
        if (grants.Count == 0 && instance.Count == 0 || grants.Any(grant => grant is null || grant.TenantId <= 0 || !IsDelegable(grant.Permission)))
            throw IntegrationCredentialGrantValidationException.Invalid("grants", "integration_grants_invalid");
        var scopes = grants.GroupBy(grant => grant.TenantId).ToArray();
        var limits = IntegrationPermissionCatalog.Limits;
        if (scopes.Length > limits.TenantScopes &&
            (currentTenantIds is null || scopes.Any(scope => !currentTenantIds.Contains(scope.Key))))
            throw IntegrationCredentialGrantValidationException.Invalid("grants", "integration_tenant_capacity", requested: scopes.Length, limit: limits.TenantScopes);
        foreach (var scope in scopes)
        {
            if (scope.Count() > limits.PermissionsPerScope)
                throw IntegrationCredentialGrantValidationException.Invalid("grants", "integration_scope_capacity",
                    requested: scope.Count(), limit: limits.PermissionsPerScope);
            if (scope.Distinct().Count() != scope.Count())
                throw IntegrationCredentialGrantValidationException.Invalid("grants", "integration_grants_invalid");
        }
        if (instance.Any(permission => !IsDelegable(permission)) || instance.Distinct(StringComparer.Ordinal).Count() != instance.Count)
            throw IntegrationCredentialGrantValidationException.Invalid("instancePermissions", "integration_grants_invalid");
    }

    private static bool IsDelegable(string permission) => !string.IsNullOrWhiteSpace(permission) &&
        string.Equals(permission, permission.Trim(), StringComparison.Ordinal) &&
        NetRatelPermissions.All.Contains(permission) && permission != NetRatelPermissions.IntegrationManagement;
}

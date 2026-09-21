using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace NetRatel.Infrastructure.Identity.Authorization;

public sealed record EffectiveAccessSnapshot(
    string? PrincipalId,
    bool IsLegacyOperator,
    bool IsInstanceAdministrator,
    IReadOnlySet<string> Permissions);

/// <summary>
/// The sole evaluator for application principal roles. It evaluates a
/// permission and its tenant scope together; callers must not union those
/// results independently.
/// </summary>
public interface IEffectiveAccessService
{
    Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default);
    Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default);
    Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default);
}

public sealed class EffectiveAccessService(NetRatelIdentityDbContext db, IConfiguration configuration) : IEffectiveAccessService
{
    private const string IntegrationCredentialIdClaimType = "netratel_integration_credential_id";

    public async Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default)
    {
        if (!NetRatelPermissions.All.Contains(permission))
        {
            return false;
        }

        var snapshot = await GetSnapshotAsync(principal, tenantId, cancellationToken).ConfigureAwait(false);
        return snapshot.IsLegacyOperator || snapshot.IsInstanceAdministrator || snapshot.Permissions.Contains(permission);
    }

    public async Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default)
    {
        if (IsLegacyOperator(principal))
        {
            return new(null, true, true, NetRatelPermissions.All);
        }

        var principalId = principal.FindFirst("netratel_principal_id")?.Value;
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return new(null, false, false, new HashSet<string>(StringComparer.Ordinal));
        }

        var credentialId = principal.FindFirst(IntegrationCredentialIdClaimType)?.Value;
        var credential = string.IsNullOrWhiteSpace(credentialId) ? null : await db.IntegrationCredentials
            .Include(candidate => candidate.Grants)
            .Include(candidate => candidate.InstanceGrants)
            .SingleOrDefaultAsync(candidate => candidate.Id == credentialId &&
                (candidate.Purpose == IntegrationCredentialPurpose.Api || candidate.Purpose == IntegrationCredentialPurpose.HttpMcp) &&
                candidate.RevokedAtUtc == null, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(credentialId) &&
            (credential is null || credential.ExpiresAtUtc <= DateTimeOffset.UtcNow || credential.OwnerPrincipalId != principalId))
        {
            return new(principalId, false, false, new HashSet<string>(StringComparer.Ordinal));
        }

        var localInstanceAdministrator = await db.Users
            .Where(user => user.PrincipalId == principalId && user.IsEnabled)
            .Select(user => user.IsInstanceAdministrator)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var assignments = await db.PrincipalRoleAssignments
            .AsNoTracking()
            .Where(assignment => assignment.PrincipalId == principalId &&
                (assignment.TenantId == null || assignment.TenantId == tenantId))
            .Select(assignment => new
            {
                assignment.TenantId,
                assignment.Role!.IsInstanceAdministratorRole,
                Permissions = assignment.Role.Permissions.Select(permission => permission.Permission)
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var permissions = assignments
            .SelectMany(assignment => assignment.Permissions)
            .ToHashSet(StringComparer.Ordinal);
        var assignedInstanceAdministrator = assignments.Any(assignment =>
            assignment.TenantId is null && assignment.IsInstanceAdministratorRole);

        if (credential is not null)
        {
            if (localInstanceAdministrator || assignedInstanceAdministrator)
            {
                permissions = NetRatelPermissions.All.ToHashSet(StringComparer.Ordinal);
            }

            var granted = (tenantId is null
                    ? credential.InstanceGrants.Select(grant => grant.Permission)
                    : credential.Grants.Where(grant => grant.TenantId == tenantId).Select(grant => grant.Permission))
                .ToHashSet(StringComparer.Ordinal);
            permissions.IntersectWith(granted);

            // A bearer credential is always attenuated. It cannot turn the
            // owner's instance-administrator status into wildcard authority.
            return new(principalId, false, false, permissions);
        }

        return new(principalId, false, localInstanceAdministrator || assignedInstanceAdministrator, permissions);
    }

    public async Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default)
    {
        var existingNames = await db.AccessRoles
            .Where(role => role.IsBuiltIn)
            .Select(role => role.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var known = existingNames.ToHashSet(StringComparer.Ordinal);

        foreach (var definition in BuiltInAccessRoleCatalog.Definitions.Where(definition => !known.Contains(definition.Name)))
        {
            var role = new AccessRole
            {
                Name = definition.Name,
                Description = definition.Description,
                IsBuiltIn = true,
                IsInstanceAdministratorRole = definition.IsInstanceAdministratorRole,
                DelegationRank = definition.DelegationRank
            };
            foreach (var permission in definition.Permissions)
            {
                role.Permissions.Add(new AccessRolePermission { Permission = permission });
            }

            db.AccessRoles.Add(role);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsLegacyOperator(ClaimsPrincipal principal)
    {
        var configuredGroup = configuration["Authorization:Oidc:AdminGroupId"]
                              ?? configuration["Authorization:Azure:AdminGroupId"]
                              ?? configuration["AzureAd:AdminGroupId"]
                              ?? configuration["Jwt:AdminGroupId"];
        return principal.Claims.Any(claim =>
                   (claim.Type is "roles" or ClaimTypes.Role) &&
                   string.Equals(claim.Value, "Operator", StringComparison.OrdinalIgnoreCase)) ||
               principal.Claims.Any(claim => claim.Type == "groups" &&
                   (string.Equals(claim.Value, "Operator", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(configuredGroup) && string.Equals(claim.Value, configuredGroup, StringComparison.OrdinalIgnoreCase))));
    }
}

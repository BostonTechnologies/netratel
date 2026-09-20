using Microsoft.AspNetCore.Authorization;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Security.Authorization;

public sealed class EffectiveAccessRequirement(
    string permission,
    bool instanceScope = false,
    string? legacyRequiredScope = null) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
    public bool InstanceScope { get; } = instanceScope;
    public string? LegacyRequiredScope { get; } = legacyRequiredScope;
}

public sealed class EffectiveAccessHandler(IEffectiveAccessService access) : AuthorizationHandler<EffectiveAccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, EffectiveAccessRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (requirement.InstanceScope)
        {
            var instanceSnapshot = await access.GetSnapshotAsync(context.User, tenantId: null).ConfigureAwait(false);
            if ((instanceSnapshot.IsLegacyOperator && HasLegacyScope(context.User, requirement.LegacyRequiredScope)) || instanceSnapshot.IsInstanceAdministrator)
            {
                context.Succeed(requirement);
            }

            return;
        }

        var tenantId = context.Resource is HttpContext http &&
                       int.TryParse(http.Request.RouteValues["tenantId"]?.ToString(), out var routeTenantId)
            ? routeTenantId
            : (int?)null;
        var snapshot = await access.GetSnapshotAsync(context.User, tenantId).ConfigureAwait(false);
        var authorized = NetRatelPermissions.All.Contains(requirement.Permission) &&
                         (snapshot.IsLegacyOperator || snapshot.IsInstanceAdministrator || snapshot.Permissions.Contains(requirement.Permission));
        if (authorized && (!snapshot.IsLegacyOperator || HasLegacyScope(context.User, requirement.LegacyRequiredScope)))
        {
            context.Succeed(requirement);
        }
    }

    private static bool HasLegacyScope(System.Security.Claims.ClaimsPrincipal principal, string? requiredScope) =>
        string.IsNullOrWhiteSpace(requiredScope) || principal.Claims
            .Where(claim => claim.Type is "scope" or "scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(scope => string.Equals(scope, requiredScope, StringComparison.Ordinal));
}

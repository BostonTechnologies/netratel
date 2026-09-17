using System.Globalization;
using System.Security.Claims;

namespace NetRatel.API.Realtime.Operations;

/// <summary>
/// Resolves tenant scope from the authenticated operator, never from a browser
/// supplied tenant identifier. ExternalService administrators retain their existing
/// all-tenant operational scope.
/// </summary>
public interface IOperationsLogTenantAuthorizer
{
    bool IsAuthorized(ClaimsPrincipal principal, int tenantId);
}

public sealed class OperationsLogTenantAuthorizer(string? adminGroupId = null) : IOperationsLogTenantAuthorizer
{
    public bool IsAuthorized(ClaimsPrincipal principal, int tenantId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (tenantId <= 0 || principal.Identity?.IsAuthenticated != true) return false;

        if (principal.Claims.Any(claim =>
                (claim.Type == "roles" || claim.Type == ClaimTypes.Role) &&
                string.Equals(claim.Value, "Operator", StringComparison.OrdinalIgnoreCase)) ||
            principal.Claims.Any(claim => claim.Type == "groups" &&
                (string.Equals(claim.Value, "Operator", StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(adminGroupId) && string.Equals(claim.Value, adminGroupId, StringComparison.OrdinalIgnoreCase)))))
        {
            return true;
        }

        return principal.FindAll("tenant_id").Any(claim =>
            int.TryParse(claim.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var allowedTenantId) &&
            allowedTenantId == tenantId);
    }
}

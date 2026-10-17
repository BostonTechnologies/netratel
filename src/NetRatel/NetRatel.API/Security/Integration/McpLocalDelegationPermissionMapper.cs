using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Operations;
using System.Security.Claims;

namespace NetRatel.API.Security.Integration;

/// <summary>
/// Converts the closed MCP operation catalogue into application permissions
/// for a local credential exchange. The result is an attenuation boundary: a
/// credential never receives a capability merely by naming an MCP scope.
/// </summary>
internal static class McpLocalDelegationPermissionMapper
{
    public static bool IsInstanceRead(string tool, string operation) => McpLocalOperationAuthorizationCatalog.IsInstanceRead(tool, operation);
    public static string? RequiredPermission(string tool, string operation) => McpLocalOperationAuthorizationCatalog.RequiredPermission(tool, operation);
    public static IReadOnlyCollection<string> RolesFor(McpOperationAccessScope scope) => McpLocalOperationAuthorizationCatalog.RolesFor(scope);
    public static IReadOnlyList<string> AdditionalPermissions(string tool, string operation) => McpLocalOperationAuthorizationCatalog.AdditionalPermissions(tool, operation);

    public static async Task<bool> AuthorizeAsync(IEffectiveAccessService access, ClaimsPrincipal principal,
        string tool, string operation, int? tenantId, CancellationToken cancellationToken)
    {
        if (RequiredPermission(tool, operation) is not { } permission ||
            !await access.AuthorizeAsync(principal, permission, tenantId, cancellationToken).ConfigureAwait(false))
            return false;
        foreach (var additional in AdditionalPermissions(tool, operation))
            if (!await access.AuthorizeAsync(principal, additional, tenantId, cancellationToken).ConfigureAwait(false))
                return false;
        return true;
    }
}

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
    public static bool IsInstanceRead(string tool, string operation) =>
        (tool, operation) is ("netratel_auth", "status") or ("netratel_health", "get") or
            ("netratel_system", "version") or ("netratel_capabilities", "get");

    public static string? RequiredPermission(string tool, string operation)
    {
        var access = McpOperationAccessCatalog.Find(tool, operation);
        if (access is null)
            return null;
        if (IsInstanceRead(tool, operation))
            return NetRatelPermissions.McpDiscoveryRead;
        if (ManagedOperationCatalog.Find(tool, operation) is { } managed)
            return managed.Permission;

        return tool switch
        {
            "netratel_policy" or "netratel_config" => NetRatelPermissions.McpPolicyAdministration,
            "netratel_tenants" or "netratel_onboarding" => NetRatelPermissions.TenantAdministration,
            "netratel_clients" => access.RequiredScope == McpOperationAccessScope.Observe
                ? NetRatelPermissions.TelemetryRead
                : NetRatelPermissions.ClientManagement,
            "netratel_files" when operation is "preview_delete" or "delete" or "confirm_delete" or
                "preview_artifact_cleanup" or "artifact_cleanup" or "confirm_artifact_cleanup" or "cleanup" => NetRatelPermissions.FileDelete,
            "netratel_files" when operation is "collect" or "preview_collect" or "confirm_collect" => NetRatelPermissions.FileRead,
            "netratel_files" => access.RequiredScope switch
            {
                McpOperationAccessScope.Files => NetRatelPermissions.FileRead,
                McpOperationAccessScope.Write => NetRatelPermissions.FileWrite,
                _ => null
            },
            "netratel_scripts" => access.RequiredScope == McpOperationAccessScope.Execute
                ? NetRatelPermissions.ScriptExecute
                : NetRatelPermissions.ScriptEdit,
            "netratel_jobs" or "netratel_job_runs" or "netratel_marker_jobs" => NetRatelPermissions.JobManagement,
            "netratel_terminal" or "netratel_commands" or "netratel_tasks" => NetRatelPermissions.TerminalAccess,
            "netratel_remote_support_v2" => NetRatelPermissions.RemoteSupport,
            "netratel_client_logs" or "netratel_client_telemetry" or "netratel_telemetry" or "netratel_logs" or "netratel_events" or "netratel_notifications" => NetRatelPermissions.TelemetryRead,
            "netratel_requests" or "netratel_search" or "netratel_access" or "netratel_health" or "netratel_system" or "netratel_capabilities" => NetRatelPermissions.ClientManagement,
            _ => null
        };
    }

    public static IReadOnlyCollection<string> RolesFor(McpOperationAccessScope scope) => scope switch
    {
        McpOperationAccessScope.Observe or McpOperationAccessScope.Files => ["Operator"],
        McpOperationAccessScope.Execute => ["AutomationOperator"],
        McpOperationAccessScope.Admin => ["PolicyAdministrator"],
        _ => []
    };

    public static IReadOnlyList<string> AdditionalPermissions(string tool, string operation) => (tool, operation) switch
    {
        ("netratel_files", "preview_copy" or "copy" or "confirm_copy") => [NetRatelPermissions.FileRead],
        ("netratel_files", "preview_move" or "move" or "confirm_move") => [NetRatelPermissions.FileDelete],
        _ => ManagedOperationCatalog.Find(tool, operation)?.AdditionalPermissions ?? []
    };

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

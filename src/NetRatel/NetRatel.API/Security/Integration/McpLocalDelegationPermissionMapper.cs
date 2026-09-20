using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Security.Integration;

/// <summary>
/// Converts the closed MCP operation catalogue into one application permission
/// for a local credential exchange. The result is an attenuation boundary: a
/// credential never receives a capability merely by naming an MCP scope.
/// </summary>
internal static class McpLocalDelegationPermissionMapper
{
    public static string? RequiredPermission(string tool, string operation)
    {
        var access = McpOperationAccessCatalog.Find(tool, operation);
        if (access is null)
            return null;

        return tool switch
        {
            "netratel_policy" or "netratel_config" => NetRatelPermissions.McpPolicyAdministration,
            "netratel_tenants" or "netratel_onboarding" => NetRatelPermissions.TenantAdministration,
            "netratel_clients" => access.RequiredScope == McpOperationAccessScope.Observe
                ? NetRatelPermissions.TelemetryRead
                : NetRatelPermissions.ClientManagement,
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
}

namespace NetRatel.Shared.Operations;

/// <summary>Explicit application grants and role attenuation for local HTTP MCP operations.</summary>
public static class McpLocalOperationAuthorizationCatalog
{
    public static bool IsInstanceRead(string tool, string operation) =>
        (tool, operation) is ("netratel_auth", "status") or ("netratel_health", "get") or
            ("netratel_system", "version") or ("netratel_capabilities", "get" or "status" or "schema") or
            ("netratel_access", "request" or "requests" or "receipt" or "resume" or "cancel" or "credential");

    public static string? RequiredPermission(string tool, string operation)
    {
        var access = McpOperationAccessCatalog.Find(tool, operation);
        if (access is null)
            return null;
        if (IsInstanceRead(tool, operation))
            return "mcp.discovery.read";
        if (ManagedOperationCatalog.Find(tool, operation) is { } managed)
            return managed.Permission;

        return tool switch
        {
            "netratel_connectivity" when operation is "settings" or "netratel" or "preview_test" => "mcp.connectivity.read",
            "netratel_connectivity" when operation is "test" => "mcp.connectivity.test",
            "netratel_policy" or "netratel_config" => "mcp.policy.admin",
            "netratel_tenants" or "netratel_onboarding" => "tenant.admin",
            "netratel_clients" => access.RequiredScope == McpOperationAccessScope.Observe
                ? "telemetry.read"
                : "client.manage",
            "netratel_files" when operation is "preview_delete" or "delete" or "confirm_delete" or
                "preview_artifact_cleanup" or "artifact_cleanup" or "confirm_artifact_cleanup" or "cleanup" => "file.delete",
            "netratel_files" when operation is "collect" or "preview_collect" or "confirm_collect" => "file.read",
            "netratel_files" => access.RequiredScope switch
            {
                McpOperationAccessScope.Files => "file.read",
                McpOperationAccessScope.Write => "file.write",
                _ => null
            },
            "netratel_scripts" => access.RequiredScope == McpOperationAccessScope.Execute
                ? "script.execute"
                : "script.edit",
            "netratel_jobs" or "netratel_job_runs" or "netratel_marker_jobs" => "job.manage",
            "netratel_terminal" or "netratel_commands" or "netratel_tasks" => "terminal.access",
            "netratel_remote_support_v2" => "remote-support.access",
            "netratel_client_logs" or "netratel_client_telemetry" or "netratel_telemetry" or "netratel_logs" or "netratel_events" or "netratel_notifications" => "telemetry.read",
            "netratel_requests" or "netratel_search" or "netratel_access" or "netratel_health" or "netratel_system" or "netratel_capabilities" => "client.manage",
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
        ("netratel_connectivity", "test") => ["mcp.connectivity.read"],
        ("netratel_files", "preview_copy" or "copy" or "confirm_copy") => ["file.read"],
        ("netratel_files", "preview_move" or "move" or "confirm_move") => ["file.delete"],
        _ => ManagedOperationCatalog.Find(tool, operation)?.AdditionalPermissions ?? []
    };

}

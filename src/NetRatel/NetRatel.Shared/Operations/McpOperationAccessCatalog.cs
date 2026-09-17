namespace NetRatel.Shared.Operations;

/// <summary>
/// Stable OAuth scope classes used by the MCP operation catalog. Hosts bind
/// these classes to their environment-specific issuer configuration; the
/// catalog itself never contains a client secret or a deployment endpoint.
/// </summary>
public enum McpOperationAccessScope
{
    Read = 1,
    Observe = 2,
    Files = 3,
    Write = 4,
    Execute = 5,
    Onboarding = 6,
    Admin = 7,
    OfflineAccess = 8,

    // Compatibility names retained while existing Dev deployments migrate to
    // the general catalog vocabulary.
    DevelopmentWrite = Write,
    DevelopmentOnboarding = Onboarding
}

/// <summary>Canonical OAuth scope names for the checked-in MCP scope classes.</summary>
public static class McpOperationAccessScopeNames
{
    public static string Canonical(McpOperationAccessScope scope) => scope switch
    {
        McpOperationAccessScope.Read => "netratel.mcp.read",
        McpOperationAccessScope.Observe => "netratel.mcp.observe",
        McpOperationAccessScope.Files => "netratel.mcp.files",
        McpOperationAccessScope.Write => "netratel.mcp.write",
        McpOperationAccessScope.Execute => "netratel.mcp.execute",
        McpOperationAccessScope.Onboarding => "netratel.mcp.onboarding",
        McpOperationAccessScope.Admin => "netratel.mcp.admin",
        McpOperationAccessScope.OfflineAccess => "offline_access",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "The MCP access scope is not catalogued.")
    };
}

/// <summary>Minimum mapped operator role for an MCP operation.</summary>
public enum McpOperationMinimumRole
{
    Operator = 1,
    Administrator = 2
}

/// <summary>
/// Maps the V2 OAuth scope classes that are newly privileged in Dev to the
/// corresponding Oidc role claims.  Legacy read, write, and onboarding
/// scopes remain scope-authorized during the documented Dev compatibility
/// migration so existing consumers retain their documented path.
/// </summary>
public static class McpOperationRoleRequirements
{
    public static bool IsSatisfied(
        string? toolName,
        string operation,
        IReadOnlySet<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (string.IsNullOrWhiteSpace(toolName))
            return true;

        var operationName = operation.Contains('/', StringComparison.Ordinal)
            ? operation[(operation.LastIndexOf('/') + 1)..]
            : operation;
        var access = McpOperationAccessCatalog.Find(toolName, operationName);
        return access is null || IsSatisfied(access.RequiredScope, roles);
    }

    public static bool IsSatisfied(
        McpOperationAccessScope requiredScope,
        IReadOnlySet<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        return requiredScope switch
        {
            McpOperationAccessScope.Observe or McpOperationAccessScope.Files =>
                HasObservationRole(roles),
            McpOperationAccessScope.Execute => roles.Contains("AutomationOperator"),
            McpOperationAccessScope.Admin => roles.Contains("PolicyAdministrator"),
            _ => true
        };
    }

    private static bool HasObservationRole(IReadOnlySet<string> roles) =>
        roles.Contains("Observer") || roles.Contains("Operator") || roles.Contains("AutomationOperator");
}

/// <summary>
/// Development compatibility requirements for deployments that still use the
/// pre-catalog profile grants. This is catalog metadata, not an additional
/// operation scope vocabulary.
/// </summary>
public enum McpDevelopmentCompatibilityScope
{
    HostRequirement = 1,
    DevelopmentWrite = 2,
    DevelopmentOnboarding = 3
}

/// <summary>
/// One catalog-owned authorization requirement. The tool and operation pair
/// is a stable transport contract; policy adapters use it as their operation
/// name rather than accepting arbitrary method names from a caller.
/// </summary>
public sealed record McpOperationAccessDescriptor(
    string ToolName,
    string OperationName,
    McpOperationAccessScope RequiredScope,
    McpOperationMinimumRole MinimumRole,
    McpDevelopmentCompatibilityScope DevelopmentCompatibilityScope);

/// <summary>
/// Single source of truth for MCP access classifications. It covers every
/// checked-in catalog operation, including stdio-only operations, so a host
/// fails closed if a newly exposed operation has no scope/role classification.
/// <c>offline_access</c> is an OAuth session capability rather than an
/// operation precondition and is therefore intentionally not assigned here.
/// </summary>
public static class McpOperationAccessCatalog
{
    public static IReadOnlyList<McpOperationAccessDescriptor> Operations { get; } =
    [
        .. Register(McpOperationAccessScope.Read, "netratel_auth", "status"),
        .. Register(McpOperationAccessScope.Read, "netratel_health", "get"),
        .. Register(McpOperationAccessScope.Read, "netratel_system", "version"),
        .. Register(McpOperationAccessScope.Read, "netratel_capabilities", "get"),
        .. Register(McpOperationAccessScope.Read, "netratel_access", "whoami", "effective", "evaluate", "target"),
        .. Register(McpOperationAccessScope.Admin, "netratel_policy", McpOperationMinimumRole.Administrator, "policies", "policy", "change_audits", "accepted_audits", "target", "matches", "evaluate", "preview_create", "confirm_create", "preview_replace", "confirm_replace", "preview_disable", "confirm_disable", "preview_revoke", "confirm_revoke", "preview_target_profile", "confirm_target_profile"),
        .. Register(McpOperationAccessScope.Observe, "netratel_clients", "presence", "binding", "telemetry", "update_attempts", "get", "capabilities", "update_metadata"),
        .. Register(McpOperationAccessScope.Execute, "netratel_clients", "preview_ping", "ping", "preview_software_update", "software_update"),
        .. Register(McpOperationAccessScope.Admin, "netratel_clients", McpOperationMinimumRole.Administrator, "preview_disable", "disable", "preview_enable", "enable", "preview_delete", "delete"),
        .. Register(McpOperationAccessScope.Files, "netratel_files", "browse", "stat", "read", "status", "artifact_status", "download"),
        .. Register(McpOperationAccessScope.Write, "netratel_files", "collect", "preview_collect", "confirm_collect", "cleanup", "preview_artifact_cleanup", "artifact_cleanup", "confirm_artifact_cleanup", "preview_write_text", "write_text", "confirm_write_text", "preview_upload", "upload", "confirm_upload", "preview_create_directory", "create_directory", "confirm_create_directory", "preview_delete", "delete", "confirm_delete", "preview_copy", "copy", "confirm_copy", "preview_move", "move", "confirm_move"),
        .. Register(McpOperationAccessScope.Observe, "netratel_client_logs", "sources", "history", "search", "tail", "preview_resync"),
        .. Register(McpOperationAccessScope.Write, "netratel_client_logs", "resync", "confirm_resync"),
        .. Register(McpOperationAccessScope.Observe, "netratel_client_telemetry", "snapshot", "stream_window"),
        .. Register(McpOperationAccessScope.Read, "netratel_jobs", "list", "get", "details", "params", "steps"),
        .. Register(McpOperationAccessScope.Write, "netratel_jobs", "create", "update", "delete", "param_add", "param_update", "param_delete", "step_add", "step_update", "step_reorder", "step_delete"),
        .. Register(McpOperationAccessScope.Observe, "netratel_job_runs", "list", "query", "get", "steps", "logs"),
        .. Register(McpOperationAccessScope.Execute, "netratel_job_runs", "start", "cancel", "delete"),
        .. Register(McpOperationAccessScope.Execute, "netratel_marker_jobs", "create", "run", "cancel", "delete"),
        .. Register(McpOperationAccessScope.Admin, "netratel_tenants", McpOperationMinimumRole.Administrator, "list", "get", "create", "update", "delete"),
        .. Register(McpOperationAccessScope.Read, "netratel_scripts", "list", "get", "params"),
        .. Register(McpOperationAccessScope.Write, "netratel_scripts", "validate", "create_marker", "update_marker", "create", "update", "parse_manifest", "delete"),
        .. Register(McpOperationAccessScope.Execute, "netratel_scripts", "run"),
        .. Register(McpOperationAccessScope.Observe, "netratel_terminal", "availability", "get", "stream", "stream_window", "diagnostics"),
        .. Register(McpOperationAccessScope.Execute, "netratel_terminal", "preview_open", "open", "send_input", "self_test", "deployment-control-plane_inspect", "fixture", "resize", "close"),
        .. Register(McpOperationAccessScope.Observe, "netratel_commands", "availability", "get"),
        .. Register(McpOperationAccessScope.Execute, "netratel_commands", "preview_execute", "execute", "cancel"),
        .. Register(McpOperationAccessScope.Onboarding, "netratel_onboarding", "collateral", "collateral_download", "get_enrollment", "list_enrollments", "create_enrollment", "revoke_enrollment"),
        .. Register(McpOperationAccessScope.Observe, "netratel_tasks", "list", "recent", "get", "logs", "logs_by_request"),
        .. Register(McpOperationAccessScope.Execute, "netratel_tasks", "create_command", "run_library_script", "cancel"),
        .. Register(McpOperationAccessScope.Read, "netratel_requests", "list", "get"),
        .. Register(McpOperationAccessScope.Write, "netratel_requests", "create", "update", "claim", "complete", "fail", "cancel"),
        .. Register(McpOperationAccessScope.Read, "netratel_search", "tenants", "scripts", "jobs", "requests", "clients", "tasks"),
        .. Register(McpOperationAccessScope.Observe, "netratel_telemetry", "overview"),
        .. Register(McpOperationAccessScope.Observe, "netratel_notifications", "list", "get", "summary", "unread_errors"),
        .. Register(McpOperationAccessScope.Write, "netratel_notifications", "mark_read"),
        .. Register(McpOperationAccessScope.Observe, "netratel_logs", "search"),
        .. Register(McpOperationAccessScope.Admin, "netratel_connectivity", "settings", "netratel"),
        .. Register(McpOperationAccessScope.Execute, "netratel_connectivity", "preview_test", "test"),
        .. Register(McpOperationAccessScope.Observe, "netratel_events", "list", "get"),
        .. Register(McpOperationAccessScope.Execute, "netratel_events", "preview_retry", "retry"),
        .. Register(McpOperationAccessScope.Admin, "netratel_events", McpOperationMinimumRole.Administrator, "preview_disable", "disable"),
        .. Register(McpOperationAccessScope.Admin, "netratel_config", McpOperationMinimumRole.Administrator, "show", "get", "set", "unset"),
        .. Register(McpOperationAccessScope.Observe, "netratel_remote_support_v2", "presence", "capabilities", "inventory"),
        .. Register(McpOperationAccessScope.Execute, "netratel_remote_support_v2", "refresh_inventory")
    ];

    /// <summary>
    /// Compatibility projection retained for callers that need to enumerate
    /// elevated operations. It includes every non-read operation, not merely
    /// the initial Development mutation subset.
    /// </summary>
    public static IReadOnlyList<McpOperationAccessDescriptor> ElevatedOperations { get; } =
        Operations.Where(entry => entry.RequiredScope != McpOperationAccessScope.Read).ToArray();

    public static McpOperationAccessDescriptor? Find(string toolName, string? operationName) =>
        string.IsNullOrWhiteSpace(operationName)
            ? null
            : Operations.SingleOrDefault(entry =>
                string.Equals(entry.ToolName, toolName, StringComparison.Ordinal) &&
                string.Equals(entry.OperationName, operationName, StringComparison.Ordinal));

    private static McpOperationAccessDescriptor[] Register(
        McpOperationAccessScope scope,
        string toolName,
        params string[] operationNames) =>
        Register(scope, toolName, McpOperationMinimumRole.Operator, operationNames);

    private static McpOperationAccessDescriptor[] Register(
        McpOperationAccessScope scope,
        string toolName,
        McpOperationMinimumRole minimumRole,
        params string[] operationNames) =>
        operationNames.Select(operationName => new McpOperationAccessDescriptor(
            toolName,
            operationName,
            scope,
            minimumRole,
            DevelopmentCompatibilityScopeFor(scope))).ToArray();

    private static McpDevelopmentCompatibilityScope DevelopmentCompatibilityScopeFor(McpOperationAccessScope scope) => scope switch
    {
        McpOperationAccessScope.Write or McpOperationAccessScope.Execute => McpDevelopmentCompatibilityScope.DevelopmentWrite,
        McpOperationAccessScope.Onboarding => McpDevelopmentCompatibilityScope.DevelopmentOnboarding,
        _ => McpDevelopmentCompatibilityScope.HostRequirement
    };
}

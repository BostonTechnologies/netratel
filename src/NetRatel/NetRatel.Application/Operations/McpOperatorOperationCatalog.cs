using NetRatel.Shared.Operations;

namespace NetRatel.Application.Operations;

/// <summary>
/// Policy metadata for one typed MCP operation. This is intentionally separate
/// from the transport catalog so API admission can make a policy decision
/// without accepting an arbitrary controller/action name from a caller.
/// </summary>
public sealed record McpOperatorOperationDescriptor(
    string ToolName,
    string OperationName,
    McpOperatorOperationFamily OperationFamily,
    McpOperatorConfirmationClass ConfirmationClass,
    bool RequiresIdempotency,
    McpOperationAccessScope RequiredScope,
    McpOperationMinimumRole MinimumRole);

/// <summary>
/// The application-side projection of the checked-in MCP operation catalog.
/// Every exposed tool operation is bound to exactly one policy family and its
/// confirmation/idempotency contract before a V2 adapter may dispatch it.
/// </summary>
public static class McpOperatorOperationCatalog
{
    public static IReadOnlyList<McpOperatorOperationDescriptor> Operations { get; } =
    [
        .. Register(McpOperatorOperationFamily.Observability, McpOperatorConfirmationClass.None, "netratel_auth", "status"),
        .. Register(McpOperatorOperationFamily.Observability, McpOperatorConfirmationClass.None, "netratel_health", "get"),
        .. Register(McpOperatorOperationFamily.Observability, McpOperatorConfirmationClass.None, "netratel_system", "version"),
        .. Register(McpOperatorOperationFamily.Observability, McpOperatorConfirmationClass.None, "netratel_capabilities", "get"),
        .. Register(McpOperatorOperationFamily.Observability, McpOperatorConfirmationClass.None, "netratel_access", "whoami", "effective", "evaluate", "target"),
        .. Register(McpOperatorOperationFamily.PolicyAdministration, McpOperatorConfirmationClass.None, "netratel_policy", "policies", "policy", "change_audits", "accepted_audits", "target", "matches", "evaluate", "preview_create", "preview_replace", "preview_disable", "preview_revoke", "preview_target_profile"),
        .. Register(McpOperatorOperationFamily.PolicyAdministration, McpOperatorConfirmationClass.PolicyAdministration, "netratel_policy", "confirm_create", "confirm_replace", "confirm_disable", "confirm_revoke", "confirm_target_profile"),
        .. Register(McpOperatorOperationFamily.ClientAdministration, McpOperatorConfirmationClass.None, "netratel_clients", "presence", "binding", "telemetry", "update_attempts", "get", "capabilities", "update_metadata", "preview_ping", "preview_software_update", "preview_disable", "preview_enable", "preview_delete"),
        .. Register(McpOperatorOperationFamily.ClientAdministration, McpOperatorConfirmationClass.RemoteExecution, "netratel_clients", "ping", "software_update"),
        .. Register(McpOperatorOperationFamily.ClientAdministration, McpOperatorConfirmationClass.Destructive, "netratel_clients", "disable", "delete"),
        .. Register(McpOperatorOperationFamily.ClientAdministration, McpOperatorConfirmationClass.StandardMutation, "netratel_clients", "enable"),
        .. Register(McpOperatorOperationFamily.FileRead, McpOperatorConfirmationClass.None, "netratel_files", "browse", "stat", "read", "status", "artifact_status", "download"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.None, "netratel_files", "preview_collect"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.StandardMutation, "netratel_files", "collect", "confirm_collect"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.None, "netratel_files", "preview_artifact_cleanup"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.Destructive, "netratel_files", "cleanup", "artifact_cleanup", "confirm_artifact_cleanup"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.None, "netratel_files", "preview_write_text"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.Destructive, "netratel_files", "write_text"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.Destructive, "netratel_files", "confirm_write_text"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.None, "netratel_files", "preview_upload"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.Destructive, "netratel_files", "upload", "confirm_upload"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.None, "netratel_files", "preview_create_directory"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.StandardMutation, "netratel_files", "create_directory", "confirm_create_directory"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.None, "netratel_files", "preview_delete"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.Destructive, "netratel_files", "delete", "confirm_delete"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.None, "netratel_files", "preview_copy", "preview_move"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.StandardMutation, "netratel_files", "copy", "confirm_copy"),
        .. Register(McpOperatorOperationFamily.FileWrite, McpOperatorConfirmationClass.Destructive, "netratel_files", "move", "confirm_move"),
        .. Register(McpOperatorOperationFamily.Observability, McpOperatorConfirmationClass.None, "netratel_client_logs", "sources", "history", "search", "tail", "preview_resync"),
        .. Register(McpOperatorOperationFamily.Observability, McpOperatorConfirmationClass.StandardMutation, "netratel_client_logs", "resync", "confirm_resync"),
        .. Register(McpOperatorOperationFamily.Observability, McpOperatorConfirmationClass.None, "netratel_client_telemetry", "snapshot", "stream_window"),
        .. Register(McpOperatorOperationFamily.AutomationRead, McpOperatorConfirmationClass.None, "netratel_jobs", "list", "get", "details", "params", "steps"),
        .. Register(McpOperatorOperationFamily.AutomationWrite, McpOperatorConfirmationClass.StandardMutation, "netratel_jobs", "create", "update", "delete", "param_add", "param_update", "param_delete", "step_add", "step_update", "step_reorder", "step_delete"),
        .. Register(McpOperatorOperationFamily.JobExecution, McpOperatorConfirmationClass.None, "netratel_job_runs", "list", "query", "get", "steps", "logs"),
        .. Register(McpOperatorOperationFamily.JobExecution, McpOperatorConfirmationClass.RemoteExecution, "netratel_job_runs", "start"),
        .. Register(McpOperatorOperationFamily.JobExecution, McpOperatorConfirmationClass.StandardMutation, "netratel_job_runs", "cancel"),
        .. Register(McpOperatorOperationFamily.JobExecution, McpOperatorConfirmationClass.Destructive, "netratel_job_runs", "delete"),
        .. Register(McpOperatorOperationFamily.AutomationWrite, McpOperatorConfirmationClass.StandardMutation, "netratel_marker_jobs", "create"),
        .. Register(McpOperatorOperationFamily.JobExecution, McpOperatorConfirmationClass.RemoteExecution, "netratel_marker_jobs", "run"),
        .. Register(McpOperatorOperationFamily.JobExecution, McpOperatorConfirmationClass.StandardMutation, "netratel_marker_jobs", "cancel"),
        .. Register(McpOperatorOperationFamily.JobExecution, McpOperatorConfirmationClass.Destructive, "netratel_marker_jobs", "delete"),
        .. Register(McpOperatorOperationFamily.TenantAdministration, McpOperatorConfirmationClass.None, "netratel_tenants", "list", "get"),
        .. Register(McpOperatorOperationFamily.TenantAdministration, McpOperatorConfirmationClass.StandardMutation, "netratel_tenants", "create", "update"),
        .. Register(McpOperatorOperationFamily.TenantAdministration, McpOperatorConfirmationClass.Destructive, "netratel_tenants", "delete"),
        .. Register(McpOperatorOperationFamily.ScriptsRead, McpOperatorConfirmationClass.None, "netratel_scripts", "list", "get", "params"),
        .. Register(McpOperatorOperationFamily.ScriptsWrite, McpOperatorConfirmationClass.None, "netratel_scripts", "validate"),
        .. Register(McpOperatorOperationFamily.ScriptsWrite, McpOperatorConfirmationClass.StandardMutation, "netratel_scripts", "create_marker", "update_marker", "create", "update", "parse_manifest"),
        .. Register(McpOperatorOperationFamily.TaskExecution, McpOperatorConfirmationClass.RemoteExecution, "netratel_scripts", "run"),
        .. Register(McpOperatorOperationFamily.ScriptsWrite, McpOperatorConfirmationClass.Destructive, "netratel_scripts", "delete"),
        .. Register(McpOperatorOperationFamily.TerminalRead, McpOperatorConfirmationClass.None, "netratel_terminal", "availability", "get", "stream", "stream_window", "diagnostics"),
        .. Register(McpOperatorOperationFamily.TerminalExecute, McpOperatorConfirmationClass.None, "netratel_terminal", "preview_open"),
        .. Register(McpOperatorOperationFamily.TerminalExecute, McpOperatorConfirmationClass.None, true, "netratel_terminal", "send_input", "resize", "close"),
        .. Register(McpOperatorOperationFamily.TerminalExecute, McpOperatorConfirmationClass.RemoteExecution, "netratel_terminal", "open", "self_test", "deployment-control-plane_inspect"),
        .. Register(McpOperatorOperationFamily.TerminalExecute, McpOperatorConfirmationClass.Destructive, "netratel_terminal", "fixture"),
        .. Register(McpOperatorOperationFamily.TaskExecution, McpOperatorConfirmationClass.None, "netratel_commands", "availability", "get", "preview_execute"),
        .. Register(McpOperatorOperationFamily.TaskExecution, McpOperatorConfirmationClass.Destructive, "netratel_commands", "execute"),
        .. Register(McpOperatorOperationFamily.TaskExecution, McpOperatorConfirmationClass.StandardMutation, "netratel_commands", "cancel"),
        .. Register(McpOperatorOperationFamily.Onboarding, McpOperatorConfirmationClass.None, "netratel_onboarding", "collateral", "collateral_download", "get_enrollment", "list_enrollments"),
        .. Register(McpOperatorOperationFamily.Onboarding, McpOperatorConfirmationClass.CredentialIssuance, "netratel_onboarding", "create_enrollment", "revoke_enrollment"),
        .. Register(McpOperatorOperationFamily.TaskExecution, McpOperatorConfirmationClass.None, "netratel_tasks", "list", "recent", "get", "logs", "logs_by_request"),
        .. Register(McpOperatorOperationFamily.TaskExecution, McpOperatorConfirmationClass.RemoteExecution, "netratel_tasks", "create_command", "run_library_script"),
        .. Register(McpOperatorOperationFamily.TaskExecution, McpOperatorConfirmationClass.StandardMutation, "netratel_tasks", "cancel"),
        .. Register(McpOperatorOperationFamily.Requests, McpOperatorConfirmationClass.None, "netratel_requests", "list", "get"),
        .. Register(McpOperatorOperationFamily.Requests, McpOperatorConfirmationClass.StandardMutation, "netratel_requests", "create", "update", "claim", "complete", "fail", "cancel"),
        .. Register(McpOperatorOperationFamily.Observability, McpOperatorConfirmationClass.None, "netratel_search", "tenants", "scripts", "jobs", "requests", "clients", "tasks"),
        .. Register(McpOperatorOperationFamily.Observability, McpOperatorConfirmationClass.None, "netratel_telemetry", "overview"),
        .. Register(McpOperatorOperationFamily.Notifications, McpOperatorConfirmationClass.None, "netratel_notifications", "list", "get", "summary", "unread_errors"),
        .. Register(McpOperatorOperationFamily.Notifications, McpOperatorConfirmationClass.StandardMutation, "netratel_notifications", "mark_read"),
        .. Register(McpOperatorOperationFamily.Observability, McpOperatorConfirmationClass.None, "netratel_logs", "search"),
        .. Register(McpOperatorOperationFamily.Connectivity, McpOperatorConfirmationClass.None, "netratel_connectivity", "settings", "netratel", "preview_test"),
        .. Register(McpOperatorOperationFamily.Connectivity, McpOperatorConfirmationClass.RemoteExecution, "netratel_connectivity", "test"),
        .. Register(McpOperatorOperationFamily.Events, McpOperatorConfirmationClass.None, "netratel_events", "list", "get", "preview_retry", "preview_disable"),
        .. Register(McpOperatorOperationFamily.Events, McpOperatorConfirmationClass.RemoteExecution, "netratel_events", "retry"),
        .. Register(McpOperatorOperationFamily.Events, McpOperatorConfirmationClass.StandardMutation, "netratel_events", "disable"),
        .. Register(McpOperatorOperationFamily.ClientAdministration, McpOperatorConfirmationClass.None, "netratel_config", "show", "get"),
        .. Register(McpOperatorOperationFamily.ClientAdministration, McpOperatorConfirmationClass.PolicyAdministration, "netratel_config", "set", "unset"),
        .. Register(McpOperatorOperationFamily.Connectivity, McpOperatorConfirmationClass.None, "netratel_remote_support_v2", "presence", "capabilities", "inventory"),
        .. Register(McpOperatorOperationFamily.Connectivity, McpOperatorConfirmationClass.RemoteExecution, "netratel_remote_support_v2", "refresh_inventory")
    ];

    public static McpOperatorOperationDescriptor? Find(string toolName, string? operationName) =>
        string.IsNullOrWhiteSpace(operationName)
            ? null
            : Operations.SingleOrDefault(entry =>
                string.Equals(entry.ToolName, toolName, StringComparison.Ordinal) &&
                string.Equals(entry.OperationName, operationName, StringComparison.Ordinal));

    private static McpOperatorOperationDescriptor[] Register(
        McpOperatorOperationFamily family,
        McpOperatorConfirmationClass confirmationClass,
        string toolName,
        params string[] operationNames) =>
        Register(family, confirmationClass, confirmationClass != McpOperatorConfirmationClass.None, toolName, operationNames);

    private static McpOperatorOperationDescriptor[] Register(
        McpOperatorOperationFamily family,
        McpOperatorConfirmationClass confirmationClass,
        bool requiresIdempotency,
        string toolName,
        params string[] operationNames) =>
        operationNames.Select(operationName =>
        {
            var access = McpOperationAccessCatalog.Find(toolName, operationName)
                ?? throw new InvalidOperationException($"MCP operation '{toolName}/{operationName}' has no access-scope classification.");
            return new McpOperatorOperationDescriptor(
                toolName,
                operationName,
                family,
                confirmationClass,
                requiresIdempotency,
                access.RequiredScope,
                access.MinimumRole);
        }).ToArray();
}

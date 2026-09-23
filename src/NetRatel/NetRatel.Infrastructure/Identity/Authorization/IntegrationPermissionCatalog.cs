namespace NetRatel.Infrastructure.Identity.Authorization;

public sealed record IntegrationPermissionDescriptor(string Id, string Label, string Description);

/// <summary>Human-readable permission descriptions shared by the delegation API and Web editor.</summary>
public static class IntegrationPermissionCatalog
{
    public static IReadOnlyList<IntegrationPermissionDescriptor> Delegable { get; } =
    [
        new(NetRatelPermissions.TelemetryRead, "Read telemetry", "View status and performance information."),
        new(NetRatelPermissions.FileRead, "Read files", "Browse and download permitted files."),
        new(NetRatelPermissions.FileWrite, "Write files", "Upload or change permitted files."),
        new(NetRatelPermissions.FileDelete, "Delete files", "Remove permitted files."),
        new(NetRatelPermissions.ScriptEdit, "Edit scripts", "Create or change scripts."),
        new(NetRatelPermissions.ScriptExecute, "Run scripts", "Execute permitted scripts on authorized targets."),
        new(NetRatelPermissions.JobManagement, "Manage jobs", "Create and manage scheduled work."),
        new(NetRatelPermissions.TerminalAccess, "Use terminal", "Open a terminal on authorized clients."),
        new(NetRatelPermissions.ClientManagement, "Manage clients", "Change client enrollment and settings."),
        new(NetRatelPermissions.RemoteSupport, "Use remote support", "Start authorized remote support sessions."),
        new(NetRatelPermissions.AuditRead, "Read audit history", "View recorded activity."),
        new(NetRatelPermissions.SecretUse, "Use secrets", "Use configured secrets without revealing their values."),
        new(NetRatelPermissions.SecretReveal, "Reveal secrets", "Read secret values where authorized."),
        new(NetRatelPermissions.ArtifactPublication, "Publish artifacts", "Publish client artifacts."),
        new(NetRatelPermissions.TenantAdministration, "Administer tenant", "Change tenant-level settings and authority."),
        new(NetRatelPermissions.UserRoleAdministration, "Manage identities", "Change user and role assignments."),
        new(NetRatelPermissions.McpDiscoveryRead, "Discover MCP capabilities", "List the MCP operations permitted by this credential."),
        new(NetRatelPermissions.McpPolicyAdministration, "Manage MCP policy", "Change MCP operation policy.")
    ];
}

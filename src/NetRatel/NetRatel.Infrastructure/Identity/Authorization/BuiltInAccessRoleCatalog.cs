namespace NetRatel.Infrastructure.Identity.Authorization;

/// <summary>
/// Built-ins are inserted only when absent. Existing definitions are never
/// widened during startup, so an upgrade cannot silently grant authority.
/// </summary>
public static class BuiltInAccessRoleCatalog
{
    public static IReadOnlyList<BuiltInAccessRoleDefinition> Definitions { get; } =
    [
        new("InstanceAdministrator", "Full instance administration.", 1000, true, NetRatelPermissions.All),
        new("TenantAdministrator", "Tenant administration and managed clients.", 800, false,
            Set(NetRatelPermissions.TenantAdministration, NetRatelPermissions.UserRoleAdministration, NetRatelPermissions.ClientManagement, NetRatelPermissions.TelemetryRead, NetRatelPermissions.ScriptEdit, NetRatelPermissions.ScriptExecute, NetRatelPermissions.JobManagement, NetRatelPermissions.TerminalAccess, NetRatelPermissions.FileRead, NetRatelPermissions.FileWrite, NetRatelPermissions.FileDelete, NetRatelPermissions.RemoteSupport, NetRatelPermissions.SecretUse, NetRatelPermissions.AuditRead)),
        new("Operator", "Operate managed clients within an assigned tenant.", 600, false,
            Set(NetRatelPermissions.ClientManagement, NetRatelPermissions.TelemetryRead, NetRatelPermissions.ScriptExecute, NetRatelPermissions.JobManagement, NetRatelPermissions.TerminalAccess, NetRatelPermissions.FileRead, NetRatelPermissions.FileWrite, NetRatelPermissions.RemoteSupport, NetRatelPermissions.SecretUse)),
        new("Observer", "Read telemetry and audit information.", 200, false,
            Set(NetRatelPermissions.TelemetryRead, NetRatelPermissions.AuditRead)),
        new("ScriptEditor", "Edit scripts without execution authority.", 300, false,
            Set(NetRatelPermissions.ScriptEdit)),
        new("IntegrationAdministrator", "Manage non-interactive integration access.", 400, false,
            Set(NetRatelPermissions.IntegrationManagement, NetRatelPermissions.McpPolicyAdministration)),
        new("Publisher", "Publish approved artifacts and updates.", 400, false,
            Set(NetRatelPermissions.ArtifactPublication))
    ];

    private static IReadOnlySet<string> Set(params string[] permissions) =>
        new HashSet<string>(permissions, StringComparer.Ordinal);
}

public sealed record BuiltInAccessRoleDefinition(
    string Name,
    string Description,
    int DelegationRank,
    bool IsInstanceAdministratorRole,
    IReadOnlySet<string> Permissions);

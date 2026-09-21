namespace NetRatel.Infrastructure.Identity.Authorization;

/// <summary>Stable permission vocabulary for product authorization.</summary>
public static class NetRatelPermissions
{
    public const string TenantAdministration = "tenant.admin";
    public const string UserRoleAdministration = "identity.admin";
    public const string ClientManagement = "client.manage";
    public const string TelemetryRead = "telemetry.read";
    public const string ScriptEdit = "script.edit";
    public const string ScriptExecute = "script.execute";
    public const string JobManagement = "job.manage";
    public const string TerminalAccess = "terminal.access";
    public const string FileRead = "file.read";
    public const string FileWrite = "file.write";
    public const string FileDelete = "file.delete";
    public const string RemoteSupport = "remote-support.access";
    public const string SecretUse = "secret.use";
    public const string SecretReveal = "secret.reveal";
    public const string AuditRead = "audit.read";
    public const string IntegrationManagement = "integration.manage";
    public const string ArtifactPublication = "artifact.publish";
    public const string McpPolicyAdministration = "mcp.policy.admin";
    public const string McpDiscoveryRead = "mcp.discovery.read";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        TenantAdministration, UserRoleAdministration, ClientManagement,
        TelemetryRead, ScriptEdit, ScriptExecute, JobManagement, TerminalAccess,
        FileRead, FileWrite, FileDelete, RemoteSupport, SecretUse, SecretReveal,
        AuditRead, IntegrationManagement, ArtifactPublication, McpPolicyAdministration, McpDiscoveryRead
    };
}

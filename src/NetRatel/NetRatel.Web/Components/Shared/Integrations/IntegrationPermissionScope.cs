namespace NetRatel.Web.Components.Shared.Integrations;

// Presentation metadata only. Every selection remains an explicit permission ID in one scope.
public sealed record IntegrationPermission(string Id, string Label, string Description)
{
    public string Group => Id.Split('.')[0] switch
    {
        "agents" or "computers" or "inventory" => "Computers and inventory",
        "jobs" or "scripts" or "automation" or "flows" => "Jobs and automation",
        "telemetry" or "monitoring" or "logs" or "services" => "Monitoring and services",
        "files" or "terminal" => "Files and terminals",
        "tenants" or "users" or "roles" or "access" => "Accounts and access",
        "mcp" => "MCP discovery",
        "notifications" or "connectors" or "integrations" => "Connections and notifications",
        _ => "Other capabilities"
    };
}
public sealed record IntegrationPermissionScope(int? TenantId, string Name, IReadOnlyList<IntegrationPermission> Permissions);
public sealed record IntegrationPermissionChange(int? TenantId, IReadOnlyList<string> Permissions, bool Selected);

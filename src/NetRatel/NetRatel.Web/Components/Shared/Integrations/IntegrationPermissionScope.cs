namespace NetRatel.Web.Components.Shared.Integrations;

// Presentation metadata only. Every selection remains an explicit permission ID in one scope.
public sealed record IntegrationPermission(string Id, string Label, string Description)
{
    public string Group => Id.Split('.')[0] switch
    {
        "agent" or "agents" or "computer" or "computers" or "inventory" or "client" or "clients" => "Computers and inventory",
        "job" or "jobs" or "script" or "scripts" or "automation" or "flow" or "flows" => "Jobs and automation",
        "telemetry" or "monitoring" or "log" or "logs" or "service" or "services" => "Monitoring and services",
        "file" or "files" or "terminal" or "remote-support" => "Files, terminals and remote support",
        "computer-use" => "Computer use automation",
        "tenant" or "tenants" or "user" or "users" or "role" or "roles" or "access" or "identity" => "Accounts and access",
        "mcp" => "MCP capabilities and policy",
        "notification" or "notifications" or "connector" or "connectors" or "integration" or "integrations" => "Connections and notifications",
        "secret" or "secrets" => "Secrets and sensitive access",
        "audit" => "Audit history",
        "artifact" or "artifacts" => "Artifacts and distribution",
        _ => "Other capabilities"
    };
}
public sealed record IntegrationPermissionScope(int? TenantId, string Name, IReadOnlyList<IntegrationPermission> Permissions);
public sealed record IntegrationPermissionChange(int? TenantId, IReadOnlyList<string> Permissions, bool Selected);

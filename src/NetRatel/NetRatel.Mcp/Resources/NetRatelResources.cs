using System.Text.Json;
using ModelContextProtocol.Server;
using NetRatel.AgentClient;
using NetRatel.Mcp.Core;

namespace NetRatel.Mcp.Resources;

[McpServerResourceType]
public sealed class NetRatelResources(INetRatelAgentClient client, NetRatelMcpHostContext hostContext)
{
    [McpServerResource(UriTemplate = "netratel://server/status", Name = "NetRatel server status", MimeType = "application/json")]
    public async Task<string> ServerStatus(CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(new { server = hostContext.ServerName, transport = hostContext.Transport.ToString(), apiBaseUrl = hostContext.Target.ApiBaseUri, catalogRevision = hostContext.Target.CatalogRevision, mutationToolsEnabled = false });

    [McpServerResource(UriTemplate = "netratel://health", Name = "NetRatel health", MimeType = "application/json")]
    public async Task<string> Health(CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await client.GetHealthAsync(cancellationToken).ConfigureAwait(false));

    [McpServerResource(UriTemplate = "netratel://config/redacted", Name = "Redacted NetRatel configuration", MimeType = "application/json")]
    public string RedactedConfiguration() => JsonSerializer.Serialize(AgentClientConfigurationResolver.Redact(client.Configuration));

    [McpServerResource(UriTemplate = "netratel://capabilities", Name = "NetRatel MCP capabilities", MimeType = "application/json")]
    public string Capabilities() => JsonSerializer.Serialize(new
    {
        catalogRevision = NetRatelMcpCatalog.Revision,
        transport = hostContext.Transport.ToString(),
        tools = NetRatelMcpCatalog.Tools.Select(ToSchema),
        resources = NetRatelMcpCatalog.Resources,
        prompts = NetRatelMcpCatalog.Prompts,
        mutationToolsEnabled = true
    });

    [McpServerResource(UriTemplate = "netratel://schemas/catalog", Name = "NetRatel MCP catalog schema", MimeType = "application/json")]
    public string CatalogSchema() => JsonSerializer.Serialize(new
    {
        revision = NetRatelMcpCatalog.Revision,
        target = new { instance = hostContext.Target.Instance, resourceUri = hostContext.Target.ResourceUri },
        tools = NetRatelMcpCatalog.Tools.Select(ToSchema),
        resources = NetRatelMcpCatalog.Resources,
        prompts = NetRatelMcpCatalog.Prompts
    });

    [McpServerResource(UriTemplate = "netratel://schemas/response", Name = "NetRatel MCP response schema", MimeType = "application/json")]
    public string ResponseSchema() => JsonSerializer.Serialize(new
    {
        type = "NetRatelToolResponse",
        fields = new[] { "success", "status", "summary", "data", "affectedIds", "error", "requiresConfirmation", "confirmation" },
        errorFields = new[] { "code", "retryable", "upstreamStatus", "allowedOperations" },
        confirmationFields = new[] { "confirmField", "requiredValue", "operation", "affectedIds" }
    });

    private static object ToSchema(NetRatelMcpToolDescriptor tool) => new
    {
        tool.Name,
        tool.Description,
        safety = tool.Safety.ToString(),
        tool.AvailableOverHttp,
        operations = tool.Operations.Select(operation => new
        {
            operation.Name,
            safety = operation.Safety.ToString(),
            operation.RequiresConfirmation,
            operation.AvailableOverHttp
        })
    };
}

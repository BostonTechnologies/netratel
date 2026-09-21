using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using NetRatel.Shared.Operations;

namespace NetRatel.Mcp.Core;

/// <summary>
/// Transport-safe catalog primitives. The HTTP host exposes these discovery
/// tools together with the catalog's API-backed operations. Development
/// mutations are published only where their target-eligibility and
/// confirmation contracts are independently enforced.
/// </summary>
[McpServerToolType]
public sealed class NetRatelMcpCatalogTools(NetRatelMcpHostContext hostContext)
{
    [McpServerTool(UseStructuredContent = true), Description("Describe the NetRatel MCP catalog and its HTTP availability. Operation must be get.")]
    public NetRatelToolResponse netratel_capabilities(string operation = "get")
        => string.Equals(operation, "get", StringComparison.Ordinal)
            ? new NetRatelToolResponse(true, "completed", "NetRatel MCP capabilities returned.", JsonSerializer.SerializeToNode(CatalogSchema(hostContext)))
            : new NetRatelToolResponse(false, "invalid_request", "Unsupported netratel_capabilities operation.",
                Error: new NetRatelToolError("unsupported_operation", false, AllowedOperations: ["get"]))
                .WithFailureContext("netratel_capabilities", operation);

    internal static object CatalogSchema(NetRatelMcpHostContext hostContext)
    {
        var tools = NetRatelMcpCatalog.Tools
            .Where(tool => IsAvailable(tool, hostContext))
            .Select(tool => ToSchema(tool, hostContext))
            .ToArray();

        return new
        {
            revision = NetRatelMcpCatalog.Revision,
            target = new { instance = hostContext.Target.Instance, resourceUri = hostContext.Target.ResourceUri },
            transport = hostContext.Transport.ToString(),
            toolCount = tools.Length,
            tools,
            resources = NetRatelMcpCatalog.Resources,
            prompts = NetRatelMcpCatalog.Prompts
        };
    }

    internal static object ResponseSchema() => new
    {
        type = "NetRatelToolResponse",
        fields = new[] { "success", "status", "summary", "data", "affectedIds", "error", "failure", "correlationId", "requiresConfirmation", "confirmation" },
        errorFields = new[] { "code", "retryable", "upstreamStatus", "allowedOperations" },
        failureFields = new[] { "code", "layer", "retryable", "requiredScopes", "requiredOperation", "target", "safeDetails", "remediation" },
        confirmationFields = new[] { "confirmField", "requiredValue", "operation", "affectedIds" }
    };

    private static bool IsAvailable(NetRatelMcpToolDescriptor tool, NetRatelMcpHostContext hostContext)
        => tool.Operations.Any(operation => IsAvailable(operation, hostContext));

    private static bool IsAvailable(NetRatelMcpOperationDescriptor operation, NetRatelMcpHostContext hostContext)
        => NetRatelMcpCatalog.IsAvailableIn(operation, hostContext) &&
           (hostContext.Transport is not NetRatelMcpTransport.StreamableHttp || NetRatelMcpCatalog.IsAvailableOverHttpIn(operation, hostContext));

    private static object ToSchema(NetRatelMcpToolDescriptor tool, NetRatelMcpHostContext hostContext)
    {
        var operations = tool.Operations
            .Where(operation => IsAvailable(operation, hostContext))
            .ToArray();
        var description = hostContext.OperatorSurfaceEnabled
            ? NetRatelMcpOperatorInstructions.Describe(tool)
            : tool.Name == "netratel_notifications" &&
                          hostContext.Transport == NetRatelMcpTransport.StreamableHttp &&
                          string.Equals(hostContext.Target.Instance, "dev", StringComparison.Ordinal)
            ? "Inspect notifications belonging to the authenticated Development operator."
            : tool.Description;

        return new
        {
            tool.Name,
            Description = description,
            safety = EffectiveSafety(operations).ToString(),
            tool.AvailableOverHttp,
            operations = operations.Select(operation =>
            {
                var access = McpOperationAccessCatalog.Find(tool.Name, operation.Name)
                    ?? throw new InvalidOperationException($"MCP operation '{tool.Name}/{operation.Name}' has no access catalog classification.");
                return new
                {
                    operation.Name,
                    safety = operation.Safety.ToString(),
                    operation.RequiresConfirmation,
                    operation.RequiresIdempotency,
                    operation.AvailableOverHttp,
                    availableIn = NetRatelMcpCatalog.EffectiveAvailableInstances(operation, hostContext),
                    requiredScope = access.RequiredScope.ToString(),
                    minimumRole = access.MinimumRole.ToString(),
                    targetModel = McpOperationTargetCatalog.Find(tool.Name, operation.Name)?.Model.ToString()
                        ?? throw new InvalidOperationException($"MCP operation '{tool.Name}/{operation.Name}' has no target-model classification.")
                };
            })
        };
    }

    private static NetRatelMcpOperationSafety EffectiveSafety(IReadOnlyList<NetRatelMcpOperationDescriptor> operations)
    {
        if (operations.Any(operation => operation.Safety == NetRatelMcpOperationSafety.Excluded))
        {
            return NetRatelMcpOperationSafety.Excluded;
        }

        if (operations.Any(operation => operation.Safety == NetRatelMcpOperationSafety.OperatorMutation))
        {
            return NetRatelMcpOperationSafety.OperatorMutation;
        }

        return operations.Any(operation => operation.Safety == NetRatelMcpOperationSafety.DevelopmentMutation)
            ? NetRatelMcpOperationSafety.DevelopmentMutation
            : NetRatelMcpOperationSafety.Read;
    }
}

[McpServerResourceType]
public sealed class NetRatelMcpCatalogResources(NetRatelMcpHostContext hostContext)
{
    [McpServerResource(UriTemplate = "netratel://server/status", Name = "NetRatel HTTP MCP status", MimeType = "application/json")]
    public string ServerStatus() => JsonSerializer.Serialize(new
    {
        server = hostContext.ServerName,
        transport = hostContext.Transport.ToString(),
        instance = hostContext.Target.Instance,
        apiBaseUrl = hostContext.Target.ApiBaseUri,
        catalogRevision = hostContext.Target.CatalogRevision,
        mutationToolsEnabled = NetRatelMcpCatalog.Tools
            .SelectMany(tool => tool.Operations)
            .Any(operation => operation.Safety != NetRatelMcpOperationSafety.Read &&
                              (hostContext.Transport != NetRatelMcpTransport.StreamableHttp ||
                               operation.IsAvailableOverHttpIn(hostContext.Target.Instance)))
    });

    [McpServerResource(UriTemplate = "netratel://health", Name = "NetRatel MCP health descriptor", MimeType = "application/json")]
    public string Health() => JsonSerializer.Serialize(new
    {
        state = "ready",
        server = hostContext.ServerName,
        transport = hostContext.Transport.ToString(),
        healthTool = "netratel_health",
        note = "Use netratel_health to retrieve the authenticated NetRatel API readiness result."
    });

    [McpServerResource(UriTemplate = "netratel://config/redacted", Name = "NetRatel MCP redacted configuration", MimeType = "application/json")]
    public string RedactedConfiguration() => JsonSerializer.Serialize(new
    {
        instance = hostContext.Target.Instance,
        apiBaseUrl = hostContext.Target.ApiBaseUri,
        resourceUri = hostContext.Target.ResourceUri,
        transport = hostContext.Transport.ToString(),
        secretsExposed = false,
        configurationMutationEnabled = false
    });

    [McpServerResource(UriTemplate = "netratel://capabilities", Name = "NetRatel MCP capabilities", MimeType = "application/json")]
    public string Capabilities() => JsonSerializer.Serialize(NetRatelMcpCatalogTools.CatalogSchema(hostContext));

    [McpServerResource(UriTemplate = "netratel://schemas/catalog", Name = "NetRatel MCP catalog schema", MimeType = "application/json")]
    public string CatalogSchema() => JsonSerializer.Serialize(NetRatelMcpCatalogTools.CatalogSchema(hostContext));

    [McpServerResource(UriTemplate = "netratel://schemas/response", Name = "NetRatel MCP response schema", MimeType = "application/json")]
    public string ResponseSchema() => JsonSerializer.Serialize(NetRatelMcpCatalogTools.ResponseSchema());
}

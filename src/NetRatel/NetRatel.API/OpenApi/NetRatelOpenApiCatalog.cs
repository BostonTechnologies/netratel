using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NetRatel.API.OpenApi;

/// <summary>Applies stable, consumer-facing taxonomy and security metadata to generated API operations.</summary>
public static class NetRatelOpenApiCatalog
{
    public static Task TransformOperationAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var description = context.Description;
        var path = description.RelativePath ?? string.Empty;
        var tag = Classify(path);
        var document = context.Document ?? throw new InvalidOperationException("OpenAPI operation transformation requires a document.");
        var tags = document.Tags ?? new HashSet<OpenApiTag>();
        tags.Add(new OpenApiTag { Name = tag });
        document.Tags = tags;
        operation.Tags = new HashSet<OpenApiTagReference> { new(tag, document, null) };
        operation.OperationId = $"{(description.HttpMethod ?? "operation").ToLowerInvariant()}_{Normalize(path)}";

        var metadata = description.ActionDescriptor.EndpointMetadata;
        if (metadata?.OfType<IAllowAnonymous>().Any() == true)
        {
            operation.Security = [];
            return Task.CompletedTask;
        }

        var policy = metadata?.OfType<IAuthorizeData>().Select(item => item.Policy).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        var scheme = policy switch
        {
            "M2MOnly" => "M2M",
            "AgentAccess" or "AgentGatewayAccess" => "Agent",
            "MachineTokenApi" => "MachineToken",
            "LocalUser" => "LocalSession",
            "McpLocalDelegationExchange" => "IntegrationCredential",
            _ => "Bearer"
        };

        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(scheme, document)] = []
            }
        ];
        return Task.CompletedTask;
    }

    private static string Classify(string path) => path.ToLowerInvariant() switch
    {
        var value when value.Contains("mcp") => "Integrations/MCP",
        var value when value.Contains("remote-support") || value.Contains("terminal") => "Remote Support",
        var value when value.Contains("telemetry") || value.Contains("logs") => "Telemetry/Logs",
        var value when value.Contains("script") || value.Contains("command") => "Scripts/Commands",
        var value when value.Contains("job") || value.Contains("task") => "Jobs/Tasks",
        var value when value.Contains("file") => "Files/Terminal",
        var value when value.Contains("health") || value.Contains("system") || value.Contains("diagnostic") => "Health/Diagnostics",
        var value when value.Contains("setup") || value.Contains("branding") => "Setup/Instance",
        var value when value.Contains("tenant") || value.Contains("access") => "Tenants/Access",
        var value when value.Contains("agent") || value.Contains("client") || value.Contains("enrollment") => "Clients/Enrollment",
        _ => "Authentication/Accounts"
    };

    private static string Normalize(string path) => string.Concat(path.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_'))
        .Trim('_')
        .Replace("__", "_", StringComparison.Ordinal);
}

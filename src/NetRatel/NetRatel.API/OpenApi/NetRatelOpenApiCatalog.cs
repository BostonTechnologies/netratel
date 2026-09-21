using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NetRatel.API.OpenApi;

/// <summary>Applies stable, consumer-facing taxonomy and security metadata to generated API operations.</summary>
public static class NetRatelOpenApiCatalog
{
    private static readonly HashSet<string> DefaultAuthenticatedSchemes = ["Bearer", "LocalSession", "IntegrationCredential"];
    private static readonly HashSet<string> InteractiveAccountSchemes = ["Bearer", "LocalSession"];
    private static readonly HashSet<string> M2MSchemes = ["M2M"];
    private static readonly HashSet<string> AgentSchemes = ["Agent"];
    private static readonly HashSet<string> MachineTokenSchemes = ["MachineToken"];
    private static readonly HashSet<string> LocalSessionSchemes = ["LocalSession"];
    private static readonly HashSet<string> ArtifactUploadSchemes = ["Bearer", "LocalSession", "M2M"];
    private static readonly HashSet<string> ArtifactDownloadSchemes = ["Bearer", "LocalSession", "M2M", "Agent"];
    private static readonly IReadOnlyDictionary<string, HashSet<string>> PolicySchemes =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["M2MOnly"] = M2MSchemes,
            ["AgentAccess"] = AgentSchemes,
            ["AgentGatewayAccess"] = AgentSchemes,
            ["MachineTokenApi"] = MachineTokenSchemes,
            ["LocalUser"] = LocalSessionSchemes,
            ["InteractiveAccount"] = InteractiveAccountSchemes,
            ["ClientArtifactsUpload"] = ArtifactUploadSchemes,
            ["HealthRead"] = ArtifactUploadSchemes,
            ["ClientArtifactsDownload"] = ArtifactDownloadSchemes,

            // These policies use the default selector and an effective-access
            // requirement. An opaque API credential is therefore an intentional
            // alternative, constrained by its durable grant tuple.
            ["InstanceAdministrator"] = DefaultAuthenticatedSchemes,
            ["TenantAdministrator"] = DefaultAuthenticatedSchemes,
            ["ClientManager"] = DefaultAuthenticatedSchemes,
            ["TelemetryReader"] = DefaultAuthenticatedSchemes,
            ["TerminalOperator"] = DefaultAuthenticatedSchemes,
            ["RemoteSupportOperator"] = DefaultAuthenticatedSchemes,
            ["ScriptEditor"] = DefaultAuthenticatedSchemes,
            ["SecretRevealer"] = DefaultAuthenticatedSchemes,
            ["AuditReader"] = DefaultAuthenticatedSchemes,
            ["CommandOperator"] = DefaultAuthenticatedSchemes,
            ["FileReader"] = DefaultAuthenticatedSchemes,
            ["FileWriter"] = DefaultAuthenticatedSchemes,
            ["ArtifactPublisher"] = DefaultAuthenticatedSchemes,
            ["McpOperatorPolicyAdmin"] = DefaultAuthenticatedSchemes,

            // Administrative assertions deliberately do not accept a delegated
            // integration credential, even though they use the default selector.
            ["Operator"] = InteractiveAccountSchemes,
            ["AkkaShadowAccess"] = InteractiveAccountSchemes,
            ["ClientArtifactsWrite"] = InteractiveAccountSchemes
        };

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
        if (!tags.Any(existing => string.Equals(existing.Name, tag, StringComparison.Ordinal)))
        {
            tags.Add(new OpenApiTag { Name = tag });
        }
        document.Tags = tags;
        operation.Tags = new HashSet<OpenApiTagReference> { new(tag, document, null) };
        operation.OperationId = $"{(description.HttpMethod ?? "operation").ToLowerInvariant()}_{Normalize(path)}";

        var metadata = description.ActionDescriptor.EndpointMetadata;
        if (metadata?.OfType<IAllowAnonymous>().Any() == true)
        {
            operation.Security = [];
            return Task.CompletedTask;
        }

        var authorization = metadata?.OfType<IAuthorizeData>().ToArray() ?? [];
        var supportedSchemes = ResolveSupportedSchemes(authorization);
        operation.Security = supportedSchemes.Select(scheme => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(scheme, document)] = []
        }).ToList();
        return Task.CompletedTask;
    }

    private static IReadOnlyList<string> ResolveSupportedSchemes(IReadOnlyCollection<IAuthorizeData> authorization)
    {
        if (authorization.Count == 0)
        {
            // The fallback policy is administrator-only and does not establish
            // the effective-access boundary required for delegated credentials.
            return Ordered(InteractiveAccountSchemes);
        }

        HashSet<string>? intersection = null;
        foreach (var requirement in authorization)
        {
            var allowed = string.IsNullOrWhiteSpace(requirement.Policy)
                ? DefaultAuthenticatedSchemes
                : PolicySchemes.TryGetValue(requirement.Policy, out var schemes)
                    ? schemes
                    : throw new InvalidOperationException($"OpenAPI security metadata has no contract for authorization policy '{requirement.Policy}'.");

            if (!string.IsNullOrWhiteSpace(requirement.AuthenticationSchemes))
            {
                allowed = allowed.Intersect(
                    requirement.AuthenticationSchemes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            }

            intersection = intersection is null
                ? new HashSet<string>(allowed, StringComparer.Ordinal)
                : intersection.Intersect(allowed, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        }

        if (intersection is null || intersection.Count == 0)
        {
            throw new InvalidOperationException("OpenAPI security metadata has no authentication scheme that satisfies every authorization requirement.");
        }

        return Ordered(intersection);
    }

    private static IReadOnlyList<string> Ordered(IEnumerable<string> schemes) => schemes
        .OrderBy(scheme => scheme, StringComparer.Ordinal)
        .ToArray();

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

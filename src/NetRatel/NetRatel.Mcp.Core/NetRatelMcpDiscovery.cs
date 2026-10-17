using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetRatel.Mcp.Core;

/// <summary>Bounded discovery of the published server contract, independent of client caches.</summary>
internal static class NetRatelMcpDiscovery
{
    internal const int MaxStructuredResponseBytes = 16 * 1024;
    private const string DigestScope = "selected_tools_input_schemas_and_operation_metadata_v1";
    private static readonly ConditionalWeakTable<NetRatelMcpHostContext, CatalogSnapshot> Snapshots = new();

    internal static NetRatelToolResponse Status(NetRatelMcpHostContext host)
        => BoundedResponse("status", "Current MCP host identity and selected catalog fingerprint returned.", StatusData(host));

    internal static object StatusData(NetRatelMcpHostContext host)
    {
        var snapshot = Snapshots.GetValue(host, BuildSnapshot);
        return new
        {
            server = host.ServerName,
            identityScope = "mcp_host",
            hostRelease = host.ReleaseIdentity,
            sourceIdentityAvailable = host.ReleaseIdentity?.SourceRevision is not null,
            surface = new
            {
                transport = host.Transport.ToString(),
                instance = host.Target.Instance,
                operatorSurfaceEnabled = NetRatelMcpToolDefinitions.UsesOperatorSchema(host)
            },
            catalog = new
            {
                revision = NetRatelMcpCatalog.Revision,
                snapshot.ToolCount,
                snapshot.OperationCount,
                resourceCount = NetRatelMcpCatalog.Resources.Count,
                promptCount = NetRatelMcpCatalog.Prompts.Count,
                digest = snapshot.Digest,
                digestAlgorithm = "SHA-256",
                digestScope = DigestScope
            },
            nativeLoadedMetadata = "not_observed",
            reconnectGuidance = "Use the client's supported reconnect, then compare this server fingerprint and native tool definitions. The server cannot refresh or certify the client's loaded metadata.",
            schemaLookup = new { tool = "netratel_capabilities", operation = "schema", requestFields = new[] { "tool", "operation" } },
            maxStructuredResponseBytes = MaxStructuredResponseBytes
        };
    }

    internal static NetRatelToolResponse Schema(NetRatelMcpHostContext host, JsonElement? request)
    {
        if (request is not { ValueKind: JsonValueKind.Object } value ||
            value.EnumerateObject().Take(3).Count() != 2 ||
            !value.TryGetProperty("tool", out var toolValue) || toolValue.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("operation", out var operationValue) || operationValue.ValueKind != JsonValueKind.String ||
            !IsSelector(toolValue.GetString()) || !IsSelector(operationValue.GetString()))
            return InvalidRequest("schema");

        var snapshot = Snapshots.GetValue(host, BuildSnapshot);
        var tool = toolValue.GetString()!;
        var operation = operationValue.GetString()!;
        if (!snapshot.Operations.TryGetValue((tool, operation), out var contract))
            return new NetRatelToolResponse(false, "invalid_request", "The exact tool/operation is not published on this MCP host. Inspect the current catalog and selected surface.",
                Error: new NetRatelToolError("unsupported_operation", false))
                .WithFailureContext("netratel_capabilities", "schema");

        return BoundedResponse("schema", "Complete input envelope and requirements for one published operation returned; the inspected operation was not invoked.", new
        {
            tool,
            operation,
            catalogRevision = NetRatelMcpCatalog.Revision,
            catalogDigest = snapshot.Digest,
            schemaDigest = contract.Digest,
            digestAlgorithm = "SHA-256",
            schemaDigestScope = "inputSchema_compact_utf8_json",
            inputSchema = contract.InputSchema,
            requirements = contract.Metadata,
            accessCheckPerformed = false,
            inspectedOperationDispatched = false,
            maxStructuredResponseBytes = MaxStructuredResponseBytes
        });
    }

    internal static bool IsSelector(string? value)
        => value is { Length: > 0 and <= 128 } &&
           value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    internal static NetRatelToolResponse InvalidRequest(string operation)
        => new NetRatelToolResponse(false, "invalid_request", "Use get or status without request, or schema with exactly request.tool and request.operation, each a bounded published name.",
            Error: new NetRatelToolError("validation_error", false))
            .WithFailureContext("netratel_capabilities", operation);

    private static NetRatelToolResponse BoundedResponse(string operation, string summary, object data)
    {
        var response = new NetRatelToolResponse(true, "completed", summary, JsonSerializer.SerializeToNode(data, JsonSerializerOptions.Web));
        if (JsonSerializer.SerializeToUtf8Bytes(response, JsonSerializerOptions.Web).Length <= MaxStructuredResponseBytes)
            return response;

        return new NetRatelToolResponse(false, "unavailable", "The complete discovery response exceeds its published byte budget. No partial schema was returned.",
            Error: new NetRatelToolError("discovery_response_too_large", false))
        {
            FailureContext = new NetRatelToolFailure("discovery_response_too_large", "catalog", false, ["netratel.mcp.read"],
                $"netratel_capabilities/{operation}", null,
                "The complete structured response would exceed 16384 UTF-8 bytes.",
                "Use the supported tools/list protocol response for the complete schema and report this catalog size regression. Do not infer omitted fields or invoke a different operation.")
        };
    }

    private static CatalogSnapshot BuildSnapshot(NetRatelMcpHostContext host)
    {
        var operationContracts = new Dictionary<(string Tool, string Operation), OperationContract>();
        var tools = new List<object>();
        foreach (var descriptor in NetRatelMcpCatalog.Tools.OrderBy(tool => tool.Name, StringComparer.Ordinal))
        {
            var selected = NetRatelMcpToolDefinitions.SelectedOperations(descriptor, host);
            if (selected.Count == 0) continue;
            var inputSchema = JsonSerializer.SerializeToElement(NetRatelMcpToolDefinitions.PublishedInputSchema(descriptor, host));
            var metadata = selected.OrderBy(operation => operation.Name, StringComparer.Ordinal)
                .Select(operation => NetRatelMcpCatalogTools.OperationMetadata(descriptor, operation, host)).ToArray();
            tools.Add(new
            {
                descriptor.Name,
                description = NetRatelMcpToolDefinitions.UsesOperatorSchema(host)
                    ? NetRatelMcpOperatorInstructions.Describe(descriptor) : descriptor.Description,
                inputSchema,
                operations = metadata
            });

            foreach (var envelope in inputSchema.GetProperty("oneOf").EnumerateArray())
            {
                var operationName = envelope.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString()!;
                var operation = selected.Single(candidate => candidate.Name == operationName);
                operationContracts.Add((descriptor.Name, operationName), new OperationContract(
                    envelope.Clone(),
                    NetRatelMcpCatalogTools.OperationMetadata(descriptor, operation, host),
                    Digest(JsonSerializer.SerializeToUtf8Bytes(envelope))));
            }
        }

        var digest = Digest(JsonSerializer.SerializeToUtf8Bytes(new
        {
            revision = NetRatelMcpCatalog.Revision,
            digestScope = DigestScope,
            tools
        }, JsonSerializerOptions.Web));
        return new CatalogSnapshot(tools.Count, operationContracts.Count, digest, operationContracts);
    }

    private static string Digest(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private sealed record OperationContract(JsonElement InputSchema, object Metadata, string Digest);
    private sealed record CatalogSnapshot(int ToolCount, int OperationCount, string Digest,
        IReadOnlyDictionary<(string Tool, string Operation), OperationContract> Operations);
}

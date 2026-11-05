using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using NetRatel.Shared.Operations;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Mcp.Core;

public static partial class NetRatelMcpToolDefinitions
{
    private static RequestDefinition ManagedRequestSchema(ManagedOperationDescriptor operation)
    {
        var properties = new JsonObject
        {
            ["tenantId"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1,
                ["maximum"] = int.MaxValue, ["description"] = "Exact authorized tenant ID from tenant discovery." }
        };
        foreach (var name in operation.RequiredProperties.Where(name => name == "agentId" || name == operation.IdProperty))
            properties[name] = new JsonObject { ["type"] = "string", ["format"] = "uuid",
                ["description"] = name == "agentId" ? "Persisted client UUID from this tenant, not its hostname." : "Exact object UUID from this tenant; use a new stable UUID when creating a rule/group." };
        if (operation.ResourceKey)
            properties["resourceKey"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 2048,
                ["description"] = "Exact monitored resource key from the observed series." };
        if (operation.Paged)
        {
            properties["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100, ["default"] = 50 };
            properties["cursor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 2048,
                ["description"] = "Opaque nextCursor from the preceding page; keep other filters unchanged." };
        }
        if (operation.Searchable)
            properties["search"] = new JsonObject { ["type"] = "string", ["maxLength"] = 256 };
        if (operation.OffsetPaged)
            properties["skip"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 100_000,
                ["description"] = "Offset for bounded investigation history pages (100 records)." };
        if (operation.SequencePaged)
            properties["after"] = NonNegativeLongIdentifier("after", "Sequence of the last event actually received. Continue paging to lastSequence; retain decimal-string cursors exactly.").Schema;
        if (operation.ApprovalTarget)
            properties["callId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256,
                ["description"] = "Exact callId from the investigation's pendingApprovals. Never invent an approval." };
        if (operation.DataType is { } dataType)
        {
            // Existing managed Flows operations advertise schema one until explicit v2 MCP
            // admission is enabled. Use the real DTO, with only that unsupported additive metadata
            // excluded from this export; never change persistence or HTTP serializer options.
            var resolver = new DefaultJsonTypeInfoResolver();
            if (operation.Tool == "netratel_flows") resolver.Modifiers.Add(LegacyFlowSchemaMetadata);
            var options = new JsonSerializerOptions(ManagedOperationCatalog.JsonOptions)
            { TypeInfoResolver = resolver };
            var schema = options.GetJsonSchemaAsNode(dataType, new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
                TransformSchemaNode = (context, node) =>
                {
                    if (operation.Tool == "netratel_flows" && context.TypeInfo.Type == typeof(FlowNodeKind) &&
                        node is JsonObject enumSchema && enumSchema["enum"] is JsonArray)
                        enumSchema["enum"] = new JsonArray("AlertRaised", "Condition", "MapIncident", "CreateIncident", "ActionGroup", "InvestigateWithAi");
                    return node;
                }
            });
            // Exported references are relative to the DTO root. Inline them
            // before embedding under the tool's operation-discriminated request.
            properties["data"] = InlineManagedSchema(schema, schema, new HashSet<string>(StringComparer.Ordinal));
        }
        return new RequestDefinition(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = properties,
            ["required"] = new JsonArray(operation.RequiredProperties.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            ["description"] = $"Requires {string.Join(", ", new[] { operation.Permission }.Concat(operation.AdditionalPermissions))} for this tenant plus current MCP operation policy. Typed data uses the same request contract as the UI."
        }, true);
    }

    private static void LegacyFlowSchemaMetadata(JsonTypeInfo type)
    {
        string[] unsupported = type.Type == typeof(FlowNodeDto)
            ? ["executionTrigger", "executionAction", "rerun", "predicate", "inputs"]
            : type.Type == typeof(FlowEdgeDto) ? ["id", "route"] : [];
        foreach (var property in type.Properties.Where(property =>
            unsupported.Contains(property.Name, StringComparer.OrdinalIgnoreCase)).ToArray())
            type.Properties.Remove(property);
    }

    private static JsonNode? InlineManagedSchema(JsonNode? node, JsonNode root, IReadOnlySet<string> references)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var pointer))
            {
                if (!pointer.StartsWith("#/", StringComparison.Ordinal) || references.Contains(pointer))
                    throw new InvalidOperationException("Managed operation DTO schemas must have acyclic local references.");
                JsonNode? target = root;
                foreach (var part in pointer[2..].Split('/'))
                {
                    var key = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                    target = target is JsonArray values && int.TryParse(key, out var index) && index >= 0 && index < values.Count
                        ? values[index] : target?[key];
                }
                if (target is null) throw new InvalidOperationException("The managed DTO schema contains an unresolved reference.");
                var expanded = InlineManagedSchema(target, root, references.Append(pointer).ToHashSet(StringComparer.Ordinal))!.AsObject();
                foreach (var property in obj.Where(property => property.Key != "$ref"))
                    expanded[property.Key] = InlineManagedSchema(property.Value, root, references);
                return expanded;
            }
            return new JsonObject(obj.Select(property => new KeyValuePair<string, JsonNode?>(property.Key,
                InlineManagedSchema(property.Value, root, references))));
        }
        if (node is JsonArray array)
            return new JsonArray(array.Select(item => InlineManagedSchema(item, root, references)).ToArray());
        return node?.DeepClone();
    }
}

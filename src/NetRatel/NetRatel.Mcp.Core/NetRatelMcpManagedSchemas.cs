using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using NetRatel.Shared.Operations;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Mcp.Core;

public static partial class NetRatelMcpToolDefinitions
{
    private static JsonObject JobTargetContractSchema(Type type)
    {
        var options = new JsonSerializerOptions(ManagedOperationCatalog.JsonOptions)
        { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var schema = options.GetJsonSchemaAsNode(type);
        return InlineManagedSchema(schema, schema, new HashSet<string>(StringComparer.Ordinal)).AsObject();
    }

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
            properties["cursor"] = new JsonObject { ["type"] = "string", ["maxLength"] = operation.MonitoringQuery ? MonitoringQueryLimits.MaximumCursorLength : 2048,
                ["description"] = "Opaque nextCursor from the preceding page; keep other filters unchanged." };
        }
        if (operation.MonitoringQuery)
        {
            properties["view"] = new JsonObject { ["type"] = "string", ["enum"] = operation.Operation == "events"
                ? new JsonArray("All") : new JsonArray(System.Enum.GetNames<MonitoringView>().Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
                ["default"] = "All", ["description"] = "Additive full-result selection. Unfiltered reads include Healthy series." };
            properties["clientId"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" };
            properties["severity"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Information", "Warning", "Critical") };
            properties["text"] = new JsonObject { ["type"] = "string", ["maxLength"] = MonitoringQueryLimits.MaximumTextLength };
            properties["fromUtc"] = new JsonObject { ["type"] = "string", ["format"] = "date-time",
                ["description"] = "Inclusive UTC lower bound: RaisedAtUtc for series, event/audit AtUtc for history." };
            properties["toExclusiveUtc"] = new JsonObject { ["type"] = "string", ["format"] = "date-time",
                ["description"] = "Exclusive UTC upper bound. Convert inclusive local end dates to the next local day start first." };
            properties["sort"] = new JsonObject { ["type"] = "string", ["enum"] = operation.Operation == "events"
                ? new JsonArray("EventTimeDescending") : new JsonArray("SeriesIdentity") };
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
                    if (operation.Tool == "netratel_monitoring" && context.TypeInfo.Type == typeof(MonitoringConditionDto) && node is JsonObject condition)
                        AnnotateMonitoringCondition(condition);
                    return node;
                }
            });
            // Exported references are relative to the DTO root. Inline them
            // before embedding under the tool's operation-discriminated request.
            properties["data"] = InlineManagedSchema(schema, schema, new HashSet<string>(StringComparer.Ordinal));
            if (operation.Tool == "netratel_monitoring" && operation.Operation is "save_rule" or "preview_targets")
            {
                properties["data"]!["description"] = MonitoringAuthoringGuidance.Text;
                properties["data"]!["examples"] = MonitoringExamples(operation.Operation == "preview_targets");
            }
            if (operation.Tool == "netratel_flows" && operation.Operation == "delete")
            {
                properties["data"]!["properties"]!["expectedRevision"]!["minimum"] = 1;
                properties["data"]!["description"] = "Graph-free deletion intent. Expected revision is the exact positive revision reviewed by the user. Requires flow.delete, Write / netratel.mcp.write, an allowing AutomationWrite policy, and Destructive confirmation. Preview with confirm false changes no business state; history retains existing retention.";
            }
        }
        return new RequestDefinition(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = properties,
            ["required"] = new JsonArray(operation.RequiredProperties.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            ["description"] = $"Requires {string.Join(", ", new[] { operation.Permission }.Concat(operation.AdditionalPermissions))} for this tenant plus current MCP operation policy. Typed data uses the same request contract as the UI."
        }, true);
    }

    private static void AnnotateMonitoringCondition(JsonObject schema)
    {
        schema["description"] = MonitoringAuthoringGuidance.Text;
        if (schema["properties"] is JsonObject fields)
        {
            fields["resourceName"]!["description"] = "Required: explicit null for CPU and all eligible disks; otherwise an exact normalized disk path or exact service name. Never empty or *.";
            fields["servicePlatform"]!["description"] = "Required: explicit null for numeric conditions; Windows or LinuxSystemd for services.";
            fields["expectedServiceStates"]!["description"] = "Required: [] for numeric conditions; explicit supported states for services.";
        }
        schema["allOf"] = JsonNode.Parse("""
        [
          {"if":{"properties":{"kind":{"enum":["CpuUsagePercent","DiskFreePercent","DiskFreeSpace"]}},"required":["kind"]},
           "then":{"properties":{"servicePlatform":{"type":"null"},"expectedServiceStates":{"maxItems":0}}}},
          {"if":{"properties":{"kind":{"const":"CpuUsagePercent"}},"required":["kind"]},
           "then":{"properties":{"resourceName":{"type":"null"},"unit":{"const":"Percent"},"breachThreshold":{"type":"number","minimum":0,"maximum":100},"recoveryThreshold":{"type":"number","minimum":0,"maximum":100}}}},
          {"if":{"properties":{"kind":{"enum":["DiskFreePercent","DiskFreeSpace"]}},"required":["kind"]},
           "then":{"properties":{"resourceName":{"type":["string","null"],"minLength":1,"pattern":"^(/|[A-Z]:)"}}}},
          {"if":{"properties":{"kind":{"const":"DiskFreePercent"}},"required":["kind"]},
           "then":{"properties":{"unit":{"const":"Percent"},"breachThreshold":{"type":"number","minimum":0,"maximum":100},"recoveryThreshold":{"type":"number","minimum":0,"maximum":100}}}}
        ]
        """);
    }

    private static JsonArray MonitoringExamples(bool preview)
    {
        var result = new JsonArray();
        foreach (var (kind, breach, recovery, severity) in new[]
        {
            (MonitoringMetricKind.CpuUsagePercent, 95, 90, MonitoringSeverity.Warning),
            (MonitoringMetricKind.DiskFreePercent, 20, 25, MonitoringSeverity.Warning),
            (MonitoringMetricKind.DiskFreePercent, 10, 15, MonitoringSeverity.Critical)
        })
        {
            var targets = new MonitoringTargetSelectionDto(MonitoringTargetMode.Selected, [], [Guid.Parse("11111111-1111-4111-8111-111111111111")]);
            var condition = new MonitoringConditionDto(kind, MonitoringNumericUnit.Percent, breach, recovery, null, null, []);
            object data = preview ? new MonitoringTargetPreviewRequest(targets, condition, TimeSpan.FromMinutes(2)) :
                new MonitoringRuleWriteDto(new(7, Guid.Parse("22222222-2222-4222-8222-222222222222"), 1, 1,
                    kind == MonitoringMetricKind.CpuUsagePercent ? "CPU warning" : severity == MonitoringSeverity.Critical ? "Disk free critical" : "Disk free warning", true, severity,
                    targets, condition, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)), 40, "Reviewed monitoring policy");
            result.Add(JsonSerializer.SerializeToNode(data, data.GetType(), ManagedOperationCatalog.JsonOptions));
        }
        return result;
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

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Shared.Contracts.SoftwareInventory;

namespace NetRatel.Shared.Operations;

/// <summary>The closed application operations shared by CLI, HTTP MCP and stdio MCP.</summary>
public sealed record ManagedOperationDescriptor(string Tool, string Operation, string Permission,
    bool Mutation, bool AgentTarget, Type? DataType = null, string? IdProperty = null,
    bool Paged = false, bool ResourceKey = false, bool OffsetPaged = false,
    bool SequencePaged = false, bool ApprovalTarget = false, bool Searchable = false)
{
    public McpOperationAccessScope Scope => Permission switch
    {
        "client.manage" => Mutation ? McpOperationAccessScope.Execute : McpOperationAccessScope.Observe,
        "flow.execute" => McpOperationAccessScope.Execute,
        "tenant.admin" => McpOperationAccessScope.Admin,
        "flow.edit" or "flow.publish" or "monitoring.manage" or "monitoring.ack" or
            "monitoring.clear" or "monitoring.bypass" => McpOperationAccessScope.Write,
        _ => McpOperationAccessScope.Observe
    };

    public IReadOnlyList<string> RequiredProperties =>
        ["tenantId", .. AgentTarget ? new[] { "agentId" } : [],
            .. IdProperty is not null ? new[] { IdProperty } : [],
            .. ResourceKey ? new[] { "resourceKey" } : [],
            .. ApprovalTarget ? new[] { "callId" } : [],
            .. DataType is not null ? new[] { "data" } : []];

    public IReadOnlyList<string> AllowedProperties =>
        [.. RequiredProperties, .. Paged ? new[] { "limit", "cursor" } : [], .. Searchable ? new[] { "search" } : [],
            .. OffsetPaged ? new[] { "skip" } : [], .. SequencePaged ? new[] { "after" } : []];

    public IReadOnlyList<string> AdditionalPermissions => (Tool, Operation) switch
    {
        ("netratel_ai", "create" or "archive") => ["telemetry.read"],
        ("netratel_ai", "send" or "respond" or "reconcile") => ["telemetry.read", "secret.use"],
        _ => []
    };

    public bool RequiresOnlineClient => Tool == "netratel_services" && Operation is "refresh" or "control";
}

public static class ManagedOperationCatalog
{
    public const int MaximumRequestBytes = 196608;
    public const int MaximumEnvelopeBytes = MaximumRequestBytes + 64;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 48
    };

    public static IReadOnlyList<ManagedOperationDescriptor> Operations { get; } =
    [
        new("netratel_software", "policy", "telemetry.read", false, false),
        new("netratel_software", "set_policy", "tenant.admin", true, false, typeof(SoftwareInventoryPolicy)),
        new("netratel_software", "status", "telemetry.read", false, true),
        new("netratel_software", "scan_status", "telemetry.read", false, true, IdProperty: "collectionId"),
        new("netratel_software", "snapshot", "telemetry.read", false, true, typeof(SoftwareInventoryQuery)),
        new("netratel_software", "refresh", "client.manage", true, true, typeof(SoftwareRefreshRequest)),
        new("netratel_software", "changes", "telemetry.read", false, true, typeof(SoftwareInventoryQuery)),
        new("netratel_software", "estate_changes", "telemetry.read", false, false, typeof(SoftwareInventoryQuery)),
        new("netratel_software", "installations", "telemetry.read", false, false, typeof(SoftwareInventoryQuery)),
        new("netratel_software", "aggregate", "telemetry.read", false, false, typeof(SoftwareInventoryQuery)),
        new("netratel_clients", "metadata", "telemetry.read", false, true),
        new("netratel_clients", "set_metadata", "client.manage", true, true, typeof(UpdateClientMetadataRequest)),
        new("netratel_services", "inventory", "telemetry.read", false, true),
        new("netratel_services", "refresh", "telemetry.read", true, true),
        new("netratel_services", "control", "client.manage", true, true, typeof(ClientServiceControlRequest)),
        new("netratel_services", "operation", "client.manage", false, true, IdProperty: "operationId"),
        new("netratel_monitoring", "permissions", "monitoring.read", false, false),
        new("netratel_monitoring", "configuration", "monitoring.read", false, false),
        new("netratel_monitoring", "summary", "monitoring.read", false, false),
        new("netratel_monitoring", "series", "monitoring.read", false, false, Paged: true),
        new("netratel_monitoring", "events", "monitoring.read", false, false, Paged: true),
        new("netratel_monitoring", "clients", "monitoring.read", false, false, Paged: true, Searchable: true),
        new("netratel_monitoring", "client_series", "monitoring.read", false, true),
        new("netratel_monitoring", "published_flows", "monitoring.read", false, false),
        new("netratel_monitoring", "preview_targets", "monitoring.manage", false, false, typeof(MonitoringTargetPreviewRequest)),
        new("netratel_monitoring", "save_rule", "monitoring.manage", true, false, typeof(MonitoringRuleWriteDto), "ruleId"),
        new("netratel_monitoring", "save_group", "monitoring.manage", true, false, typeof(MonitoringGroupWriteDto), "groupId"),
        new("netratel_monitoring", "delete_rule", "monitoring.manage", true, false, typeof(MonitoringDeleteDto), "ruleId"),
        new("netratel_monitoring", "delete_group", "monitoring.manage", true, false, typeof(MonitoringDeleteDto), "groupId"),
        new("netratel_monitoring", "save_silence", "monitoring.bypass", true, false, typeof(MonitoringBypassWriteDto), "bypassId"),
        new("netratel_monitoring", "delete_silence", "monitoring.bypass", true, false, typeof(MonitoringDeleteDto), "bypassId"),
        new("netratel_monitoring", "acknowledge", "monitoring.ack", true, true, typeof(MonitoringOperatorActionDto), "ruleId", ResourceKey: true),
        new("netratel_monitoring", "clear", "monitoring.clear", true, true, typeof(MonitoringOperatorActionDto), "ruleId", ResourceKey: true),
        new("netratel_monitoring", "acknowledge_offline", "monitoring.ack", true, false, typeof(MonitoringOperatorActionDto), "ruleId"),
        new("netratel_monitoring", "clear_offline", "monitoring.clear", true, false, typeof(MonitoringOperatorActionDto), "ruleId"),
        new("netratel_flows", "list", "flow.read", false, false),
        new("netratel_flows", "get", "flow.read", false, false, IdProperty: "flowId"),
        new("netratel_flows", "template", "flow.read", false, false),
        new("netratel_flows", "connectors", "flow.read", false, false),
        new("netratel_flows", "validate", "flow.read", false, false, typeof(FlowGraphDto)),
        new("netratel_flows", "dry_run", "flow.read", false, false, typeof(FlowDryRunRequest)),
        new("netratel_flows", "create", "flow.edit", true, false, typeof(FlowCreateRequest)),
        new("netratel_flows", "save_draft", "flow.edit", true, false, typeof(FlowSaveDraftRequest), "flowId"),
        new("netratel_flows", "clone", "flow.edit", true, false, typeof(FlowCloneRequest), "flowId"),
        new("netratel_flows", "set_enabled", "flow.edit", true, false, typeof(FlowEnabledRequest), "flowId"),
        new("netratel_flows", "publish", "flow.publish", true, false, typeof(FlowRevisionRequest), "flowId"),
        new("netratel_flows", "versions", "flow.read", false, false, IdProperty: "flowId"),
        new("netratel_flows", "version", "flow.read", false, false, IdProperty: "versionId"),
        new("netratel_flows", "runs", "flow.read", false, false, IdProperty: "flowId"),
        new("netratel_flows", "run", "flow.read", false, false, IdProperty: "runId"),
        new("netratel_ai", "connection", "telemetry.read", false, false),
        new("netratel_ai", "pair", "tenant.admin", true, false, typeof(NetclawPairRequest)),
        new("netratel_ai", "test_connection", "tenant.admin", true, false),
        new("netratel_ai", "set_enabled", "tenant.admin", true, false, typeof(NetclawEnabledRequest)),
        new("netratel_ai", "clients", "telemetry.read", false, false),
        new("netratel_ai", "list", "telemetry.read", false, false, OffsetPaged: true),
        new("netratel_ai", "get", "telemetry.read", false, false, IdProperty: "investigationId", SequencePaged: true),
        new("netratel_ai", "create", "client.manage", true, false, typeof(InvestigationCreateRequest)),
        new("netratel_ai", "send", "client.manage", true, false, typeof(InvestigationMessageRequest), "investigationId"),
        new("netratel_ai", "respond", "client.manage", true, false, typeof(InvestigationApprovalRequest), "investigationId", ApprovalTarget: true),
        new("netratel_ai", "reconcile", "client.manage", true, false, IdProperty: "investigationId"),
        new("netratel_ai", "archive", "client.manage", true, false, typeof(InvestigationArchiveRequest), "investigationId")
    ];

    public static ManagedOperationDescriptor? Find(string tool, string operation) =>
        Operations.SingleOrDefault(item => item.Tool == tool && item.Operation == operation);

    public static bool TryValidate(ManagedOperationDescriptor descriptor, JsonElement request, out string? error)
    {
        error = null;
        if (request.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(request.GetRawText()) > MaximumRequestBytes)
            return Invalid("request must be a bounded object.", out error);
        var names = request.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Except(descriptor.AllowedProperties, StringComparer.Ordinal).Any() ||
            descriptor.RequiredProperties.Except(names, StringComparer.Ordinal).Any())
            return Invalid("Supply only the properties in the selected operation schema, including every required property.", out error);
        if (request.GetProperty("tenantId").ValueKind != JsonValueKind.Number ||
            !request.GetProperty("tenantId").TryGetInt32(out var tenantId) || tenantId <= 0)
            return Invalid("tenantId must be a positive integer.", out error);
        foreach (var name in descriptor.RequiredProperties.Where(name => name == "agentId" || name == descriptor.IdProperty))
            if (request.GetProperty(name).ValueKind != JsonValueKind.String ||
                !request.GetProperty(name).TryGetGuid(out var id) || id == Guid.Empty)
                return Invalid($"{name} must be a nonempty UUID from this tenant.", out error);
        if (request.TryGetProperty("limit", out var limit) &&
            (limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out var count) || count is < 1 or > 100))
            return Invalid("limit must be between 1 and 100.", out error);
        if (request.TryGetProperty("skip", out var skip) &&
            (skip.ValueKind != JsonValueKind.Number || !skip.TryGetInt32(out var offset) || offset is < 0 or > 100_000))
            return Invalid("skip must be between 0 and 100000.", out error);
        if (request.TryGetProperty("after", out var after) && !TryReadSequence(after, out _))
            return Invalid("after must be a nonnegative Int64 sequence, optionally encoded as a decimal string.", out error);
        if (request.TryGetProperty("callId", out var callId) &&
            (callId.ValueKind != JsonValueKind.String || callId.GetString() is not { Length: > 0 and <= 256 } call || call.Any(char.IsControl)))
            return Invalid("callId must be the bounded exact identifier from pendingApprovals.", out error);
        if (request.TryGetProperty("search", out var search) &&
            (search.ValueKind != JsonValueKind.String || search.GetString() is not { Length: <= 256 } searchText || searchText.Any(char.IsControl)))
            return Invalid("search must contain at most 256 characters without control characters.", out error);
        foreach (var name in new[] { "cursor", "resourceKey" })
            if (request.TryGetProperty(name, out var value) &&
                (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 2048 } text || text.Any(char.IsControl)))
                return Invalid($"{name} must be bounded text without control characters.", out error);
        if (descriptor.ResourceKey && string.IsNullOrWhiteSpace(request.GetProperty("resourceKey").GetString()))
            return Invalid("resourceKey must identify the exact monitored resource.", out error);
        if (descriptor.DataType is { } dataType)
        {
            try
            {
                if (request.GetProperty("data").Deserialize(dataType, JsonOptions) is null)
                    return Invalid("data must contain the typed request body.", out error);
            }
            catch (JsonException)
            {
                return Invalid("data does not match the operation's typed request schema.", out error);
            }
        }
        return true;
    }

    private static bool Invalid(string message, out string? error) { error = message; return false; }

    public static bool TryReadSequence(JsonElement value, out long sequence)
    {
        sequence = 0;
        return (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out sequence) ||
            value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out sequence)) && sequence >= 0;
    }
}

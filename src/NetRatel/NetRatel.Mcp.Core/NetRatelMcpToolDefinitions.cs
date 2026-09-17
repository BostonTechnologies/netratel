using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace NetRatel.Mcp.Core;

/// <summary>
/// Creates the HTTP MCP tools from the checked-in catalog rather than exposing
/// the SDK's reflection-generated open request envelope. Each catalogued read
/// operation is represented by a closed, discriminated input schema.
/// </summary>
public static class NetRatelMcpToolDefinitions
{
    private const string EnvelopeDescription = "Use operation to select exactly one catalogued operation. Put operation-specific fields only in request.";
    private static readonly Type[] HttpHandlerTypes = [typeof(NetRatelMcpCatalogTools), typeof(NetRatelMcpOperationalTools)];

    /// <summary>
    /// Creates the exact Streamable HTTP tool surface, including only the
    /// catalogued operations available to the selected environment. Development
    /// mutations retain their independently enforced target-eligibility and
    /// confirmation contracts.
    /// </summary>
    public static IReadOnlyList<McpServerTool> CreateForHttp(string instance = "dev")
        => CreateForHttp(instance, string.Equals(instance, "prod", StringComparison.Ordinal));

    /// <summary>
    /// Creates the exact Streamable HTTP surface for one immutable host. A
    /// Development host can explicitly select the same V2 operator operations
    /// as Production without changing its target instance.
    /// </summary>
    public static IReadOnlyList<McpServerTool> CreateForHttp(NetRatelMcpHostContext hostContext)
    {
        ArgumentNullException.ThrowIfNull(hostContext);
        return CreateForHttp(hostContext.Target.Instance, hostContext.OperatorSurfaceEnabled, hostContext);
    }

    private static IReadOnlyList<McpServerTool> CreateForHttp(
        string instance,
        bool operatorSurfaceEnabled,
        NetRatelMcpHostContext? hostContext = null)
        => Create(
            tool => tool.AvailableOverHttp && tool.Operations.Any(operation =>
                IsAvailableOverHttp(operation, instance, operatorSurfaceEnabled, hostContext)),
            operation => IsAvailableOverHttp(operation, instance, operatorSurfaceEnabled, hostContext),
            _ => HttpHandlerTypes,
            instance,
            operatorSurfaceEnabled);

    /// <summary>
    /// Creates every stdio tool from the same catalog as HTTP. The caller
    /// supplies the two intentionally stdio-only compatibility handlers; their
    /// operation contracts are still generated here rather than by reflection.
    /// </summary>
    public static IReadOnlyList<McpServerTool> CreateForStdio(IReadOnlyDictionary<string, Type> compatibilityHandlerTypes, string instance = "dev")
        => Create(
            tool => tool.Operations.Any(operation => operation.IsAvailableIn(instance)),
            operation => operation.IsAvailableIn(instance),
            tool => tool.Name switch
            {
                "netratel_notifications" => [typeof(NetRatelMcpStdioNotificationTools)],
                "netratel_config" or "netratel_remote_support_v2" => CompatibilityHandler(tool.Name, compatibilityHandlerTypes),
                _ => HttpHandlerTypes
            },
            instance,
            operatorSurfaceEnabled: string.Equals(instance, "prod", StringComparison.Ordinal));

    private static bool IsAvailableOverHttp(
        NetRatelMcpOperationDescriptor operation,
        string instance,
        bool operatorSurfaceEnabled,
        NetRatelMcpHostContext? hostContext)
        => hostContext is not null
            ? NetRatelMcpCatalog.IsAvailableOverHttpIn(operation, hostContext)
            : operatorSurfaceEnabled && string.Equals(instance, "dev", StringComparison.Ordinal)
                ? operation.IsAvailableOverHttpIn("prod")
                : operation.IsAvailableOverHttpIn(instance);

    private static IReadOnlyList<Type> CompatibilityHandler(string tool, IReadOnlyDictionary<string, Type> compatibilityHandlerTypes)
        => compatibilityHandlerTypes.TryGetValue(tool, out var type)
            ? [type]
            : throw new InvalidOperationException($"The stdio compatibility handler for '{tool}' was not supplied.");

    private static IReadOnlyList<McpServerTool> Create(
        Func<NetRatelMcpToolDescriptor, bool> includeTool,
        Func<NetRatelMcpOperationDescriptor, bool> includeOperation,
        Func<NetRatelMcpToolDescriptor, IReadOnlyList<Type>> handlerTypes,
        string instance,
        bool operatorSurfaceEnabled)
    {
        return NetRatelMcpCatalog.Tools
            .Where(includeTool)
            .Select(tool => Create(tool, handlerTypes(tool), includeOperation, instance, operatorSurfaceEnabled))
            .OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static McpServerTool Create(
        NetRatelMcpToolDescriptor descriptor,
        IReadOnlyList<Type> toolTypes,
        Func<NetRatelMcpOperationDescriptor, bool> includeOperation,
        string instance,
        bool operatorSurfaceEnabled)
    {
        var method = toolTypes
            .Select(type => type.GetMethod(descriptor.Name, BindingFlags.Instance | BindingFlags.Public))
            .SingleOrDefault(candidate => candidate is not null)
            ?? throw new InvalidOperationException($"No shared MCP handler was found for catalogued tool '{descriptor.Name}'.");
        var operations = descriptor.Operations.Where(includeOperation).ToArray();
        if (operations.Length == 0)
        {
            throw new InvalidOperationException($"Catalogued tool '{descriptor.Name}' has no selected operations.");
        }

        var options = new McpServerToolCreateOptions
        {
            Name = descriptor.Name,
            Title = Title(descriptor.Name),
            Description = operatorSurfaceEnabled ? NetRatelMcpOperatorInstructions.Describe(descriptor) : descriptor.Description,
            ReadOnly = operations.All(operation => operation.Safety == NetRatelMcpOperationSafety.Read),
            Destructive = false,
            Idempotent = descriptor.Name is "netratel_notifications" || operations.All(operation => operation.Safety == NetRatelMcpOperationSafety.Read),
            OpenWorld = string.Equals(descriptor.Name, "netratel_search", StringComparison.Ordinal),
            UseStructuredContent = true,
            OutputSchema = JsonSerializer.SerializeToElement(ResponseSchema())
        };
        var tool = McpServerTool.Create(
            method,
            context => CreateHandler(context.Services, method.DeclaringType!),
            options);
        tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(InputSchema(descriptor.Name, operations, instance, operatorSurfaceEnabled));
        return tool;
    }

    private static JsonObject InputSchema(string tool, IReadOnlyList<NetRatelMcpOperationDescriptor> operations, string instance, bool operatorSurfaceEnabled)
    {
        var alternatives = new JsonArray();
        foreach (var operation in operations)
        {
            var request = RequestSchema(tool, operation.Name, instance, operatorSurfaceEnabled);
            if (tool == "netratel_scripts" && instance == "dev" && operatorSurfaceEnabled && request is not null)
            {
                if (operation.Name is "create" or "update" or "parse_manifest" or "delete")
                    request = new RequestDefinition(new JsonObject { ["oneOf"] = new JsonArray(request.Schema, CanonicalScriptRequest(operation.Name).Schema) }, true);
                else if (operation.Name == "list")
                {
                    request.Schema["properties"]!["afterId"] = NonNegativeLongIdentifier("afterId", "Exclusive canonical script ID cursor; use the last returned scriptId.").Schema;
                    request.Schema["properties"]!["limit"] = Integer("limit", "Maximum canonical scripts per page.", 1, 100).Schema;
                }
            }
            var properties = new JsonObject
            {
                ["operation"] = new JsonObject
                {
                    ["const"] = operation.Name,
                    ["description"] = $"Select the {operation.Name} operation."
                }
            };
            var required = new JsonArray("operation");
            if (request is not null)
            {
                properties["request"] = request.Schema;
                if (request.Required) required.Add("request");
            }
            if (operation.RequiresConfirmation)
            {
                properties["confirm"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "Set true to execute this mutation. Omit or set false to receive a no-call confirmation preview."
                };
            }

            alternatives.Add(new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = properties,
                ["required"] = required
            });
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["description"] = EnvelopeDescription,
            ["oneOf"] = alternatives
        };
    }

    private static RequestDefinition? RequestSchema(string tool, string operation, string instance, bool operatorSurfaceEnabled) => (tool, operation) switch
    {
        ("netratel_clients", "presence") when operatorSurfaceEnabled => ProductionClientTargetRequest(),
        ("netratel_clients", "presence") => Request([
            Integer("tenantId", "Optional positive tenant identifier.", 1, int.MaxValue),
            String("search", "Optional bounded V2 client-presence search text.", 512),
            Boolean("online", "Optional current online-state filter."),
            Integer("limit", "Optional bounded result limit from 1 through 100.", 1, 100)
        ]),
        ("netratel_access", "target" or "effective") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true)
        ]),
        ("netratel_access", "evaluate") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredPatternString("tool", "Catalogued target tool name to evaluate without dispatching.", "^[A-Za-z0-9_-]{1,128}$", 128),
            RequiredPatternString("operation", "Catalogued target operation to evaluate without dispatching.", "^[A-Za-z0-9_-]{1,128}$", 128)
        ]),
        ("netratel_policy", "policies") => Request([
            String("environment", "Optional exact policy environment: Development or Production.", 16),
            Integer("tenantId", "Optional positive tenant identifier.", 1, int.MaxValue)
        ]),
        ("netratel_policy", "policy") => RequiredRequest([
            Uuid("policyId", "Persisted operator policy identifier.", true)
        ]),
        ("netratel_policy", "change_audits") => Request([
            Integer("tenantId", "Optional positive tenant identifier.", 1, int.MaxValue),
            Uuid("policyId", "Optional operator policy identifier.", false),
            Uuid("agentId", "Optional persisted V2 agent identifier.", false)
        ]),
        ("netratel_policy", "accepted_audits") => Request([
            Integer("tenantId", "Optional positive tenant identifier.", 1, int.MaxValue),
            Uuid("agentId", "Optional persisted V2 agent identifier.", false),
            String("subject", "Optional bounded exact delegated subject filter.", 256),
            Integer("limit", "Optional result limit from 1 through 250.", 1, 250)
        ]),
        ("netratel_policy", "target" or "matches") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true)
        ]),
        ("netratel_policy", "evaluate") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredPatternString("tool", "Catalogued target tool name to evaluate for the signed delegated PolicyAdministrator.", "^[A-Za-z0-9_-]{1,128}$", 128),
            RequiredPatternString("operation", "Catalogued target operation to evaluate without dispatching it.", "^[A-Za-z0-9_-]{1,128}$", 128)
        ]),
        ("netratel_policy", "preview_create") => RequiredRequest([
            ("policy", PolicyDraftSchema(), true)
        ]),
        ("netratel_policy", "confirm_create") => RequiredRequest([
            ("policy", PolicyDraftSchema(), true),
            RequiredPatternString("planToken", "Opaque plan token returned by preview_create.", "^[A-Za-z0-9_-]{32,128}$", 128),
            RequiredPatternString("idempotencyKey", "Opaque idempotency key returned by preview_create.", "^[A-Za-z0-9_-]{32,128}$", 128)
        ]),
        ("netratel_policy", "preview_replace") => RequiredRequest([
            Uuid("policyId", "Persisted operator policy identifier to replace.", true),
            PositiveLongIdentifier("expectedVersion", "Current positive policy version to replace."),
            ("policy", PolicyDraftSchema(), true)
        ]),
        ("netratel_policy", "confirm_replace") => RequiredRequest([
            Uuid("policyId", "Persisted operator policy identifier to replace.", true),
            PositiveLongIdentifier("expectedVersion", "Current positive policy version to replace."),
            ("policy", PolicyDraftSchema(), true),
            RequiredPatternString("planToken", "Opaque plan token returned by preview_replace.", "^[A-Za-z0-9_-]{32,128}$", 128),
            RequiredPatternString("idempotencyKey", "Opaque idempotency key returned by preview_replace.", "^[A-Za-z0-9_-]{32,128}$", 128)
        ]),
        ("netratel_policy", "preview_disable" or "preview_revoke") => RequiredRequest([
            Uuid("policyId", "Persisted operator policy identifier to disable or revoke.", true),
            PositiveLongIdentifier("expectedVersion", "Current positive policy version to disable or revoke."),
            ("target", TargetSelectorSchema(), true)
        ]),
        ("netratel_policy", "confirm_disable" or "confirm_revoke") => RequiredRequest([
            Uuid("policyId", "Persisted operator policy identifier to disable or revoke.", true),
            PositiveLongIdentifier("expectedVersion", "Current positive policy version to disable or revoke."),
            ("target", TargetSelectorSchema(), true),
            RequiredPatternString("planToken", "Opaque plan token returned by the matching preview.", "^[A-Za-z0-9_-]{32,128}$", 128),
            RequiredPatternString("idempotencyKey", "Opaque idempotency key returned by the matching preview.", "^[A-Za-z0-9_-]{32,128}$", 128)
        ]),
        ("netratel_policy", "preview_target_profile") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            ("classification", IntegerEnum(1, 5, "Server-owned non-Unknown target classification enum value."), true),
            StringArray("tags", "Bounded distinct target-profile tags.", 0, 32, 128),
            Integer("expectedVersion", "Omit for a new profile; otherwise supply the current positive profile version.", 1, int.MaxValue)
        ]),
        ("netratel_policy", "confirm_target_profile") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            ("classification", IntegerEnum(1, 5, "Server-owned non-Unknown target classification enum value."), true),
            StringArray("tags", "Bounded distinct target-profile tags.", 0, 32, 128),
            Integer("expectedVersion", "Omit for a new profile; otherwise supply the current positive profile version.", 1, int.MaxValue),
            RequiredPatternString("planToken", "Opaque plan token returned by preview_target_profile.", "^[A-Za-z0-9_-]{32,128}$", 128),
            RequiredPatternString("idempotencyKey", "Opaque idempotency key returned by preview_target_profile.", "^[A-Za-z0-9_-]{32,128}$", 128)
        ]),
        ("netratel_clients", "binding" or "get" or "capabilities" or "telemetry" or "update_attempts" or "update_metadata" or "preview_ping" or "preview_software_update" or "preview_enable") when operatorSurfaceEnabled => ProductionClientTargetRequest(),
        ("netratel_clients", "ping") when operatorSurfaceEnabled => ProductionClientPingConfirmRequest(),
        ("netratel_clients", "software_update") when operatorSurfaceEnabled => ProductionClientSoftwareUpdateConfirmRequest(),
        ("netratel_clients", "preview_disable") when operatorSurfaceEnabled => ProductionClientDisablePreviewRequest(),
        ("netratel_clients", "disable") when operatorSurfaceEnabled => ProductionClientDisableConfirmRequest(),
        ("netratel_clients", "preview_delete") when operatorSurfaceEnabled => ProductionClientDeletePreviewRequest(),
        ("netratel_clients", "delete") when operatorSurfaceEnabled => ProductionClientDeleteConfirmRequest(),
        ("netratel_clients", "enable") when operatorSurfaceEnabled => ProductionClientEnableConfirmRequest(),
        ("netratel_clients", "binding") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true)
        ]),
        ("netratel_files", "browse") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", operatorSurfaceEnabled ? "Canonical absolute path inside an admitted read root." : "Canonical absolute path inside the persisted Development fixture.", 4096),
            Integer("pageSize", "Optional result limit from 1 through 100.", 1, 100)
        ]),
        ("netratel_files", "stat" or "read") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", operatorSurfaceEnabled ? "Canonical absolute file path inside an admitted read root." : "Absolute UTF-8 text path inside the persisted Development fixture.", 4096)
        ]),
        ("netratel_files", "artifact_status" or "download") when operatorSurfaceEnabled => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            Uuid("artifactId", "Caller-bound file artifact identifier.", true)
        ]),
        ("netratel_files", "preview_collect") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", "Canonical existing regular-file path inside a policy-admitted read root.", 4096)
        ]),
        ("netratel_files", "confirm_collect") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", "The unchanged canonical regular-file path from preview_collect.", 4096),
            RequiredPatternString("planToken", "Opaque plan token returned by preview_collect.", "^[A-Za-z0-9_-]{32,128}$", 128),
            RequiredPatternString("idempotencyKey", "Opaque idempotency key returned by preview_collect.", "^[A-Za-z0-9_-]{32,128}$", 128)
        ]),
        ("netratel_files", "preview_artifact_cleanup") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            Uuid("artifactId", "Caller-bound file artifact identifier.", true)
        ]),
        ("netratel_files", "confirm_artifact_cleanup") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            Uuid("artifactId", "Caller-bound file artifact identifier.", true),
            RequiredPatternString("planToken", "Opaque plan token returned by preview_artifact_cleanup.", "^[A-Za-z0-9_-]{32,128}$", 128),
            RequiredPatternString("idempotencyKey", "Opaque idempotency key returned by preview_artifact_cleanup.", "^[A-Za-z0-9_-]{32,128}$", 128)
        ]),
        ("netratel_files", "preview_write_text") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", "Canonical absolute path inside a policy-admitted write root.", 4096),
            RequiredString("text", "UTF-8 text to atomically write after an explicit destructive confirmation.", 16384)
        ]),
        ("netratel_files", "confirm_write_text") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", "The unchanged canonical path from preview_write_text.", 4096),
            RequiredString("text", "The unchanged UTF-8 text from preview_write_text.", 16384),
            RequiredString("planToken", "Opaque server-issued preview token.", 128),
            RequiredString("idempotencyKey", "Opaque server-issued idempotency key.", 128)
        ]),
        ("netratel_files", "preview_upload") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", "Canonical absolute path inside a policy-admitted write root.", 4096),
            RequiredPatternString("contentBase64", "Canonical base64 content to atomically upload after an explicit destructive confirmation.", "^[A-Za-z0-9+/]*={0,2}$", 87384)
        ]),
        ("netratel_files", "confirm_upload") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", "The unchanged canonical path from preview_upload.", 4096),
            RequiredPatternString("contentBase64", "The unchanged canonical base64 content from preview_upload.", "^[A-Za-z0-9+/]*={0,2}$", 87384),
            RequiredString("planToken", "Opaque server-issued preview token.", 128),
            RequiredString("idempotencyKey", "Opaque server-issued idempotency key.", 128)
        ]),
        ("netratel_files", "preview_create_directory") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", "New canonical directory path inside a policy-admitted write root.", 4096)
        ]),
        ("netratel_files", "confirm_create_directory") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", "The unchanged new canonical directory path from preview_create_directory.", 4096),
            RequiredString("planToken", "Opaque server-issued preview token.", 128),
            RequiredString("idempotencyKey", "Opaque server-issued idempotency key.", 128)
        ]),
        ("netratel_files", "preview_delete") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", "Existing canonical file or empty directory inside a policy-admitted write root. Deletion is non-recursive.", 4096)
        ]),
        ("netratel_files", "confirm_delete") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", "The unchanged existing canonical file or empty directory from preview_delete.", 4096),
            RequiredString("planToken", "Opaque server-issued preview token.", 128),
            RequiredString("idempotencyKey", "Opaque server-issued idempotency key.", 128)
        ]),
        ("netratel_files", "preview_copy") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("sourcePath", "Existing canonical regular-file path inside a policy-admitted read root.", 4096),
            RequiredString("destinationPath", "Distinct new canonical regular-file path inside a policy-admitted write root. Existing files are never replaced.", 4096)
        ]),
        ("netratel_files", "confirm_copy") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("sourcePath", "The unchanged existing regular-file path from preview_copy.", 4096),
            RequiredString("destinationPath", "The unchanged distinct new regular-file path from preview_copy.", 4096),
            RequiredString("planToken", "Opaque server-issued preview token.", 128),
            RequiredString("idempotencyKey", "Opaque server-issued idempotency key.", 128)
        ]),
        ("netratel_files", "preview_move") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("sourcePath", "Existing canonical regular-file path inside a policy-admitted write root. The move removes this source after success.", 4096),
            RequiredString("destinationPath", "Distinct new canonical regular-file path inside a policy-admitted write root. Existing files are never replaced.", 4096)
        ]),
        ("netratel_files", "confirm_move") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("sourcePath", "The unchanged existing regular-file path from preview_move.", 4096),
            RequiredString("destinationPath", "The unchanged distinct new regular-file path from preview_move.", 4096),
            RequiredString("planToken", "Opaque server-issued preview token.", 128),
            RequiredString("idempotencyKey", "Opaque server-issued idempotency key.", 128)
        ]),
        ("netratel_files", "collect") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("path", "Absolute marker-owned file path inside the persisted Development fixture.", 4096),
            RequiredPatternString("marker", "MCP-QA campaign marker that must match the collected fixture file name.", "^MCP-QA-[A-Za-z0-9_-]{1,121}$", 128)
        ]),
        ("netratel_files", "status" or "download" or "cleanup") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            Uuid("artifactId", "Development artifact identifier.", true)
        ]),
        ("netratel_client_logs", "sources") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true)
        ]),
        ("netratel_client_logs", "history") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("sourceId", "Advertised client log source identifier.", 128),
            String("cursor", "Optional opaque history cursor.", 256),
            Integer("pageSize", "Optional bounded record limit from 1 through 100.", 1, 100),
            String("severity", "Optional exact severity filter advertised by the selected source.", 64),
            String("prefix", "Optional exact prefix filter advertised by the selected source.", 256),
            String("text", "Optional bounded log text filter.", 512)
        ]),
        ("netratel_client_logs", "search") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("sourceId", "Advertised client log source identifier.", 128),
            RequiredString("text", "Bounded log text to search for.", 512),
            String("cursor", "Optional opaque history cursor.", 256),
            Integer("pageSize", "Optional bounded record limit from 1 through 100.", 1, 100),
            String("severity", "Optional exact severity filter advertised by the selected source.", 64),
            String("prefix", "Optional exact prefix filter advertised by the selected source.", 256)
        ]),
        ("netratel_client_logs", "tail") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("sourceId", "Advertised client log source identifier.", 128),
            Integer("windowSeconds", "Optional bounded tail window from 1 through 15 seconds.", 1, 15),
            Integer("maxRecords", "Optional bounded record limit from 1 through 100.", 1, 100)
        ]),
        ("netratel_client_logs", "resync") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("sourceId", "Advertised client log source identifier to refresh through the current gateway.", 128)
        ]),
        ("netratel_client_logs", "preview_resync") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("sourceId", "Advertised client log source identifier to bind to the server-issued resync plan.", 128)
        ]),
        ("netratel_client_logs", "confirm_resync") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("sourceId", "Unchanged source identifier from preview_resync.", 128),
            RequiredString("planToken", "Opaque server-issued resync confirmation plan token.", 128),
            RequiredString("idempotencyKey", "Opaque server-issued idempotency key for this exact resync plan.", 128)
        ]),
        ("netratel_client_telemetry", "snapshot") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true)
        ]),
        ("netratel_client_telemetry", "stream_window") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            Integer("windowSeconds", "Optional bounded sample window from 1 through 15 seconds.", 1, 15),
            Integer("maxSamples", "Optional bounded sample limit from 1 through 20.", 1, 20)
        ]),
        ("netratel_clients", "update_attempts") => Request(ClientUpdateAttemptFields()),
        ("netratel_clients", "update_metadata") => null,
        ("netratel_clients", "telemetry") => ClientTelemetryRequest(),
        ("netratel_jobs", "list") => operatorSurfaceEnabled ? ProductionJobTargetRequest() : Request(JobListFields()),
        ("netratel_jobs", "get" or "details" or "params" or "steps") => operatorSurfaceEnabled ? ProductionJobReferenceRequest() : RequiredRequest([PositiveLongIdentifier("jobId", "Positive Development job identifier.")]),
        ("netratel_jobs", "create" or "update" or "delete" or "param_add" or "param_update" or "param_delete" or "step_add" or "step_update" or "step_reorder" or "step_delete") => ProductionJobMutationRequest(operation),
        ("netratel_job_runs", "list") => operatorSurfaceEnabled ? ProductionJobRunListRequest() : Request(JobRunListFields()),
        ("netratel_job_runs", "query") => operatorSurfaceEnabled ? ProductionJobRunListRequest() : Request(JobRunQueryFields()),
        ("netratel_job_runs", "get" or "steps") => operatorSurfaceEnabled ? ProductionJobRunReferenceRequest() : RequiredRequest([PositiveIdentifier("jobRunId", "Positive Development job-run identifier.")]),
        ("netratel_job_runs", "logs") => operatorSurfaceEnabled ? ProductionJobRunReferenceRequest() : RequiredRequest([PositiveIdentifier("jobRunId", "Positive Development job-run identifier."), NonNegativeIntegerIdentifier("ordinal", "Non-negative Development job-step ordinal.")]),
        ("netratel_job_runs", "start") => ProductionJobRunStartRequest(),
        ("netratel_job_runs", "cancel" or "delete") => ProductionJobRunActionRequest(),
        ("netratel_tasks", "list" or "recent") when operatorSurfaceEnabled => ProductionTaskListRequest(),
        ("netratel_tasks", "get") when operatorSurfaceEnabled => ProductionTaskReferenceRequest(),
        ("netratel_tasks", "logs") when operatorSurfaceEnabled => ProductionTaskLogsRequest(),
        ("netratel_tasks", "logs_by_request") when operatorSurfaceEnabled => ProductionTaskLogsByRequestRequest(),
        ("netratel_tasks", "create_command" or "run_library_script" or "cancel") => ProductionTaskMutationRequest(operation),
        ("netratel_requests", "list") => ProductionRequestListRequest(),
        ("netratel_requests", "get") => ProductionRequestReferenceRequest(),
        ("netratel_requests", "create" or "update" or "claim" or "complete" or "fail" or "cancel") => ProductionRequestMutationRequest(operation),
        ("netratel_marker_jobs", "create") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive Development tenant identifier."),
            Uuid("agentId", "Persisted Development V2 agent identifier.", true),
            PositiveLongIdentifier("scriptId", "Positive target-owned Development marker script identifier.")
        ]),
        ("netratel_marker_jobs", "run" or "delete") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive Development tenant identifier."),
            Uuid("agentId", "Persisted Development V2 agent identifier.", true),
            PositiveLongIdentifier("jobId", "Positive target-owned Development marker job identifier.")
        ]),
        ("netratel_marker_jobs", "cancel") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive Development tenant identifier."),
            Uuid("agentId", "Persisted Development V2 agent identifier.", true),
            PositiveLongIdentifier("jobId", "Positive target-owned Development marker job identifier."),
            PositiveIdentifier("runId", "Positive target-owned marker job-run identifier.")
        ]),
        ("netratel_tenants", "list") => operatorSurfaceEnabled ? Request([
            Integer("cursor", "Optional non-negative page cursor.", 0, int.MaxValue),
            Integer("limit", "Optional result limit from 1 through 100.", 1, 100)
        ]) : null,
        ("netratel_tenants", "get") => RequiredRequest([PositiveIntIdentifier("tenantId", "Positive tenant identifier.")]),
        ("netratel_tenants", "create" or "update") => RequiredRequest(TenantMutationFields(operation, includeIdentity: operation == "update")),
        ("netratel_tenants", "delete") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Tenant identifier to delete only after dependent impact review."),
            PositiveLongIdentifier("expectedVersion", "Current tenant ETag version; stale deletes are rejected."),
            ("cascade", new JsonObject { ["type"] = "boolean", ["description"] = "Explicitly select false: cascading tenant deletion is not supported." }, true),
            ("planToken", OpaquePlanToken("planToken", "Opaque token returned by the matching tenant delete preview.").Schema, false),
            ("idempotencyKey", OpaquePlanToken("idempotencyKey", "Opaque key returned by the matching tenant delete preview.").Schema, false)
        ]),
        ("netratel_scripts", "list") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true)
        ]),
        ("netratel_scripts", "get" or "params") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            PositiveLongIdentifier("scriptId", "Positive caller-owned script identifier.")
        ]),
        ("netratel_scripts", "validate") => RequiredRequest(ProductionScriptDraftFields()),
        ("netratel_scripts", "create") => RequiredRequest(ProductionScriptDraftFields(includePlanCredentials: true)),
        ("netratel_scripts", "update") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            PositiveLongIdentifier("scriptId", "Owned script identifier."),
            PositiveLongIdentifier("expectedVersion", "Current script ETag version; stale writes are rejected."),
            .. ProductionScriptDraftFields(includeTarget: false, includePlanCredentials: true)
        ]),
        ("netratel_scripts", "parse_manifest") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            PositiveLongIdentifier("scriptId", "Owned script identifier."),
            NonNegativeLongIdentifier("expectedVersion", "Required in Production: current ETag version; omitted by Development marker compatibility."),
            String("manifestJson", "Required in Production: bounded JSON object manifest. Omitted by Development marker compatibility.", 16 * 1024),
            OpaquePlanToken("planToken", "Required in confirmed parse after preview."),
            OpaquePlanToken("idempotencyKey", "Required in confirmed parse after preview.")
        ]),
        ("netratel_scripts", "run") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            PositiveLongIdentifier("scriptId", "Owned script identifier."),
            NonNegativeLongIdentifier("version", "Required in Production: exact current script version."),
            PatternString("contentHash", "Required in Production: exact current SHA-256 content hash.", "^[A-Fa-f0-9]{64}$", 64),
            ("parameters", new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string", ["maxLength"] = 1024 }, ["description"] = "Optional typed values. Secret parameters accept reference names only, never secret values." }, false),
            OpaquePlanToken("planToken", "Required in confirmed run after preview."),
            OpaquePlanToken("idempotencyKey", "Required in confirmed run after preview.")
        ]),
        ("netratel_scripts", "delete") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            PositiveLongIdentifier("scriptId", "Owned script identifier."),
            NonNegativeLongIdentifier("expectedVersion", "Required in Production: current ETag version; omitted by Development marker compatibility."),
            OpaquePlanToken("planToken", "Required in confirmed delete after preview."),
            OpaquePlanToken("idempotencyKey", "Required in confirmed delete after preview.")
        ]),
        ("netratel_scripts", "create_marker") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive Development tenant identifier."),
            Uuid("agentId", "Persisted Development V2 agent identifier.", true),
            RequiredPatternString("marker", "Safe campaign marker used in the server-generated harmless script.", "^[A-Za-z0-9_-]{1,128}$", 128),
            String("name", "Optional bounded marker-script name.", 120),
            String("description", "Optional bounded marker-script description.", 512),
            Enum("executionMode", "Optional server-selected execution mode. cancellation_probe adds a fixed fifteen-second delay for the Dev-only cancellation reliability check; callers cannot set a duration.", "standard", "cancellation_probe"),
            RequiredEnum("shell", "Generated harmless script shell.", "sh", "powershell")
        ]),
        ("netratel_scripts", "update_marker") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive Development tenant identifier."),
            Uuid("agentId", "Persisted Development V2 agent identifier.", true),
            PositiveLongIdentifier("scriptId", "Positive target-owned Development marker script identifier."),
            PatternString("marker", "Optional safe replacement campaign marker.", "^[A-Za-z0-9_-]{1,128}$", 128),
            String("name", "Optional bounded marker-script name.", 120),
            String("description", "Optional bounded marker-script description.", 512)
        ]),
        ("netratel_terminal", "availability") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive Development tenant identifier."),
            Uuid("agentId", "Persisted Development V2 agent identifier.", true)
        ]),
        ("netratel_terminal", "preview_open") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("shell", "Policy-allowlisted shell type.", 32),
            RequiredString("workingDirectory", "Exact policy-allowlisted working directory.", 4096),
            Integer("columns", "Optional terminal column count from 40 through 300.", 40, 300),
            Integer("rows", "Optional terminal row count from 10 through 120.", 10, 120)
        ]),
        ("netratel_terminal", "open") => TerminalOpenRequest(),
        ("netratel_terminal", "get" or "close") => TerminalSessionRequest(),
        ("netratel_terminal", "self_test" or "deployment-control-plane_inspect") => RequiredRequest([
            RequiredPatternString("sessionId", "Target-owned Development terminal session identifier.", "^[a-f0-9]{32}$", 32)
        ]),
        ("netratel_terminal", "fixture") => RequiredRequest([
            RequiredPatternString("sessionId", "Target-owned Development terminal session identifier.", "^[a-f0-9]{32}$", 32),
            RequiredEnum("action", "Server-generated fixture action.", "create", "delete"),
            RequiredPatternString("marker", "Safe marker used in the server-generated fixture file.", "^[A-Za-z0-9_-]{1,128}$", 128)
        ]),
        ("netratel_terminal", "stream") => RequiredRequest([
            RequiredPatternString("sessionId", "Target-owned Development terminal session identifier.", "^[a-f0-9]{32}$", 32),
            Integer("windowSeconds", "Optional bounded output window from 1 through 15 seconds.", 1, 15),
            Integer("maxRecords", "Optional output-record limit from 1 through 100.", 1, 100)
        ]),
        ("netratel_terminal", "stream_window") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredPatternString("sessionId", "Owned terminal session identifier.", "^[A-Fa-f0-9]{32}$", 32),
            Integer("windowSeconds", "Maximum wait for new output, 1 through 15 seconds; retained output returns immediately.", 1, 15),
            Integer("maxRecords", "Optional output-record limit from 1 through 100.", 1, 100),
            PatternString("afterSequence", "Decimal-string nextSequence from the previous response. Omit to read from the retained beginning. Carry forward to avoid repeats; gap signals missing frames.", "^[0-9]{1,20}$", 20)
        ]),
        ("netratel_terminal", "diagnostics") => ProductionTerminalSessionRequest(),
        ("netratel_terminal", "send_input") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredPatternString("sessionId", "Owned terminal session identifier.", "^[A-Fa-f0-9]{32}$", 32),
            RequiredString("input", "Bounded UTF-8 terminal input; this value is never written to policy or audit records.", 16 * 1024)
        ]),
        ("netratel_terminal", "resize") => TerminalResizeRequest(),
        ("netratel_commands", "availability") => ProductionCommandTargetRequest(),
        ("netratel_commands", "preview_execute") => ProductionCommandExecuteRequest(),
        ("netratel_commands", "execute") => ProductionCommandConfirmRequest(),
        ("netratel_commands", "get" or "cancel") => ProductionCommandReferenceRequest(),
        ("netratel_onboarding", "collateral" or "collateral_download") => OnboardingCollateralRequest(),
        ("netratel_onboarding", "get_enrollment") => OnboardingEnrollmentReadRequest(),
        ("netratel_onboarding", "revoke_enrollment") => OnboardingRevokeRequest(),
        ("netratel_onboarding", "create_enrollment") => OnboardingCreateRequest(),
        ("netratel_onboarding", "list_enrollments") => OnboardingListRequest(),
        ("netratel_tasks", "list") => RequiredRequest([RequiredString("requestId", "Task request identifier.")]),
        ("netratel_tasks", "recent") => Request(TaskRecentFields()),
        ("netratel_tasks", "get") => RequiredRequest([PositiveLongIdentifier("taskId", "Positive V2 task identifier.")]),
        ("netratel_tasks", "logs") => RequiredRequest(TaskLogFields("taskId", "Positive V2 task identifier.")),
        ("netratel_tasks", "logs_by_request") => RequiredRequest(TaskLogFields("requestId", "Request identifier.")),
        ("netratel_search", "clients" or "tenants") when operatorSurfaceEnabled => Request([String("q", "Optional search query.", 512), Integer("offset", "Use nextOffset from the previous response; defaults to zero.", 0, 1_000_000), Integer("limit", "Maximum returned records; defaults to 25.", 1, 100)]),
        ("netratel_search", _) => Request([String("q", "Optional search query.", 512)]),
        ("netratel_notifications", "get") when operatorSurfaceEnabled => RequiredRequest([Uuid("id", "Notification UUID.", true)]),
        ("netratel_notifications", "get") => RequiredRequest([RequiredString("id", "Notification identifier.")]),
        ("netratel_notifications", "mark_read") when operatorSurfaceEnabled => RequiredRequest([
            RequiredStringArray("ids", "Notification UUIDs to mark read.", 1, 200),
            PatternString("planToken", "confirmation plan token returned by the preview.", "^[A-Za-z0-9_-]{32,128}$", 128),
            PatternString("idempotencyKey", "idempotency key returned by the preview.", "^[A-Za-z0-9_-]{32,128}$", 128)
        ]),
        ("netratel_notifications", "mark_read") => RequiredRequest([RequiredStringArray("ids", "Notification identifiers to mark read.", 1, 200)]),
        ("netratel_connectivity", "preview_test") => null,
        ("netratel_connectivity", "test") => ProductionConnectivityTestConfirmRequest(),
        ("netratel_config", "get" or "unset") => RequiredRequest([RequiredEnum("key", "Allowlisted persisted stdio configuration key.", "apiBaseUrl", "oidcScope")]),
        ("netratel_config", "set") => RequiredRequest([
            RequiredEnum("key", "Allowlisted persisted stdio configuration key.", "apiBaseUrl", "oidcScope"),
            RequiredString("value", "New value for the allowlisted persisted configuration key.", 2048)
        ]),
        ("netratel_remote_support_v2", "presence" or "capabilities" or "inventory") when operatorSurfaceEnabled => ProductionClientTargetRequest(),
        ("netratel_remote_support_v2", "refresh_inventory") when operatorSurfaceEnabled => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            PatternString("planToken", "Opaque credential returned by the refresh preview.", "^[A-Za-z0-9_-]{32,128}$", 128),
            PatternString("idempotencyKey", "Opaque credential returned by the refresh preview.", "^[A-Za-z0-9_-]{32,128}$", 128)
        ]),
        ("netratel_remote_support_v2", "presence") => Request([
            Integer("tenantId", "Optional positive tenant identifier.", 1, int.MaxValue),
            String("search", "Optional bounded host or user search text.", 512),
            Boolean("online", "Optional current online-state filter.")
        ]),
        ("netratel_remote_support_v2", "capabilities" or "inventory" or "refresh_inventory") => RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true)
        ]),
        ("netratel_logs", "search") => Request([
            NonNegativeLongIdentifier("since", "Optional exclusive log sequence cursor."),
            String("level", "Optional log level.", 64),
            String("contains", "Optional bounded redacted text.", 512),
            String("correlationId", "Optional correlation identifier."),
            Integer("limit", "Optional result limit from 1 through 200.", 1, 200)
        ]),
        ("netratel_events", "get" or "preview_retry" or "preview_disable") => RequiredRequest([Uuid("eventId", "Persisted event UUID.", true)]),
        ("netratel_events", "retry" or "disable") => ProductionEventConfirmRequest(),
        ("netratel_notifications" or "netratel_events", "list") when operatorSurfaceEnabled => Request([
            Integer("page", "One-based page number; defaults to 1.", 1, 10_000),
            Integer("pageSize", "Page size from 1 through 100; defaults to 20.", 1, 100)
        ]),
        ("netratel_auth", "status") or
        ("netratel_health", "get") or
        ("netratel_system", "version") or
        ("netratel_capabilities", "get") or
        ("netratel_access", "whoami") or
        ("netratel_config", "show") or
        ("netratel_telemetry", "overview") or
        ("netratel_notifications", "list" or "summary" or "unread_errors") or
        ("netratel_connectivity", "settings" or "netratel") or
        ("netratel_events", "list") => null,
        _ => throw new InvalidOperationException($"No HTTP input schema is defined for {tool}/{operation}.")
    };

    private static JsonObject ResponseSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["success"] = new JsonObject { ["type"] = "boolean" },
            ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("completed", "confirmation_required", "invalid_request", "failed") },
            ["summary"] = new JsonObject { ["type"] = "string" },
            ["data"] = true,
            ["affectedIds"] = NullableStringArraySchema(),
            ["error"] = new JsonObject
            {
                ["oneOf"] = new JsonArray(
                    new JsonObject { ["type"] = "null" },
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["code"] = new JsonObject { ["type"] = "string" },
                            ["retryable"] = new JsonObject { ["type"] = "boolean" },
                            ["upstreamStatus"] = new JsonObject { ["type"] = "integer" },
                            ["allowedOperations"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                        },
                        ["required"] = new JsonArray("code", "retryable")
                    })
            },
            ["failure"] = new JsonObject
            {
                ["oneOf"] = new JsonArray(
                    new JsonObject { ["type"] = "null" },
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["code"] = new JsonObject { ["type"] = "string" },
                            ["layer"] = new JsonObject { ["type"] = "string" },
                            ["retryable"] = new JsonObject { ["type"] = "boolean" },
                            ["requiredScopes"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                            ["requiredOperation"] = new JsonObject { ["type"] = "string" },
                            ["target"] = true,
                            ["safeDetails"] = new JsonObject { ["type"] = "string" },
                            ["remediation"] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = new JsonArray("code", "layer", "retryable", "requiredScopes", "requiredOperation", "target", "safeDetails", "remediation")
                    })
            },
            ["correlationId"] = new JsonObject { ["type"] = "string" },
            ["requiresConfirmation"] = new JsonObject { ["type"] = "boolean" },
            ["confirmation"] = new JsonObject
            {
                ["oneOf"] = new JsonArray(
                    new JsonObject { ["type"] = "null" },
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["confirmField"] = new JsonObject { ["type"] = "string" },
                            ["requiredValue"] = new JsonObject { ["type"] = "boolean" },
                            ["operation"] = new JsonObject { ["type"] = "string" },
                            ["affectedIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                        },
                        ["required"] = new JsonArray("confirmField", "requiredValue", "operation", "affectedIds")
                    })
            },
        },
        ["required"] = new JsonArray("success", "status", "summary")
    };

    private static RequestDefinition Request(IEnumerable<(string Name, JsonObject Schema, bool Required)> properties)
        => CreateRequest(properties, false);

    private static RequestDefinition RequiredRequest(IEnumerable<(string Name, JsonObject Schema, bool Required)> properties)
        => CreateRequest(properties, true);

    private static RequestDefinition TerminalOpenRequest()
    {
        var development = RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive Development tenant identifier."),
            Uuid("agentId", "Persisted Development V2 agent identifier.", true),
            RequiredEnum("shell", "Generated Development terminal test shell.", "sh", "powershell"),
            Integer("cols", "Optional terminal column count from 40 through 300.", 40, 300),
            Integer("rows", "Optional terminal row count from 10 through 120.", 10, 120)
        ]).Schema;
        var production = RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredString("shell", "Policy-allowlisted shell type.", 32),
            RequiredString("workingDirectory", "Exact policy-allowlisted working directory.", 4096),
            String("planToken", "Opaque credential returned by preview_open.", 128),
            String("idempotencyKey", "Opaque credential returned by preview_open.", 128),
            Integer("columns", "Optional terminal column count from 40 through 300.", 40, 300),
            Integer("rows", "Optional terminal row count from 10 through 120.", 10, 120)
        ]).Schema;
        return OneOfRequest("Use the Development terminal form or the preview-confirm form; do not mix them.", development, production);
    }

    private static RequestDefinition TerminalSessionRequest()
    {
        var development = RequiredRequest([
            RequiredPatternString("sessionId", "Target-owned Development terminal session identifier.", "^[a-f0-9]{32}$", 32)
        ]).Schema;
        return OneOfRequest("Use the Development session form or the target-and-session form.", development, ProductionTerminalSessionRequest().Schema);
    }

    private static RequestDefinition ProductionTerminalSessionRequest() => RequiredRequest([
        PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
        Uuid("agentId", "Persisted V2 agent identifier.", true),
        RequiredPatternString("sessionId", "Owned terminal session identifier.", "^[A-Fa-f0-9]{32}$", 32)
    ]);

    private static RequestDefinition ProductionCommandTargetRequest() => RequiredRequest([
        PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
        Uuid("agentId", "Persisted V2 agent identifier.", true)
    ]);

    private static RequestDefinition ProductionCommandExecuteRequest() => RequiredRequest([
        PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
        Uuid("agentId", "Persisted V2 agent identifier.", true),
        RequiredEnum("shell", "Policy-allowlisted command shell.", "pwsh", "powershell", "windows-powershell", "windows_powershell", "bash", "sh", "cmd"),
        RequiredString("command", "One bounded command. It is sent only to the selected agent and is never persisted by the operator ownership record.", 32 * 1024),
        RequiredString("workingDirectory", "Exact policy-allowlisted working directory.", 4096),
        StringArray("environmentReferences", "Optional names of pre-provisioned client-local environment values; values are never accepted.", 1, 32, 128),
        Integer("timeoutSeconds", "Optional timeout bounded by the target policy, up to 3600 seconds.", 1, 3600),
        Integer("maximumOutputBytes", "Optional output bound, constrained by target policy and capped at 49152 bytes.", 1, 48 * 1024)
    ]);

    private static RequestDefinition ProductionCommandConfirmRequest()
    {
        var request = ProductionCommandExecuteRequest();
        var properties = request.Schema["properties"]!.AsObject();
        properties["planToken"] = new JsonObject { ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 128, ["description"] = "Opaque credential returned by preview_execute." };
        properties["idempotencyKey"] = new JsonObject { ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 128, ["description"] = "Opaque credential returned by preview_execute." };
        request.Schema["required"]!.AsArray().Add("planToken");
        request.Schema["required"]!.AsArray().Add("idempotencyKey");
        return request;
    }

    private static RequestDefinition ProductionCommandReferenceRequest() => RequiredRequest([
        PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
        Uuid("agentId", "Persisted V2 agent identifier.", true),
        RequiredPatternString("commandId", "Owned command identifier.", "^[A-Fa-f0-9]{32}$", 32)
    ]);

    private static RequestDefinition TerminalResizeRequest()
    {
        var development = RequiredRequest([
            RequiredPatternString("sessionId", "Target-owned Development terminal session identifier.", "^[a-f0-9]{32}$", 32),
            RequiredInteger("cols", "Terminal column count from 40 through 300.", 40, 300),
            RequiredInteger("rows", "Terminal row count from 10 through 120.", 10, 120)
        ]).Schema;
        var production = RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier.", true),
            RequiredPatternString("sessionId", "Owned terminal session identifier.", "^[A-Fa-f0-9]{32}$", 32),
            RequiredInteger("columns", "Terminal column count from 40 through 300.", 40, 300),
            RequiredInteger("rows", "Terminal row count from 10 through 120.", 10, 120)
        ]).Schema;
        return OneOfRequest("Use the Development resize form or the target-and-session form.", development, production);
    }

    private static RequestDefinition OnboardingCollateralRequest()
    {
        var development = RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive Development tenant identifier."),
            Uuid("agentId", "Persisted Development V2 agent identifier.", true),
            RequiredEnum("runtime", "Installer platform runtime.", "linux-x64", "win-x64")
        ]).Schema;
        var production = RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            RequiredEnum("runtime", "Installer platform runtime.", "linux-x64", "win-x64")
        ]).Schema;
        return OneOfRequest("Use the target-owned Development form or the tenant-scoped pre-enrollment form; do not mix them.", development, production);
    }

    private static RequestDefinition OnboardingEnrollmentReadRequest()
    {
        var development = RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive Development tenant identifier."),
            Uuid("agentId", "Persisted Development V2 agent identifier.", true),
            Uuid("enrollmentCodeId", "Target-owned Development enrollment-code identifier.", true)
        ]).Schema;
        var production = RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("enrollmentCodeId", "Tenant-scoped enrollment-code identifier.", true)
        ]).Schema;
        return OneOfRequest("Use the target-owned Development enrollment form or the tenant-scoped form; do not mix them.", development, production);
    }

    private static RequestDefinition OnboardingRevokeRequest()
    {
        var development = RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive Development tenant identifier."),
            Uuid("agentId", "Persisted Development V2 agent identifier.", true),
            Uuid("enrollmentCodeId", "Target-owned Development enrollment-code identifier.", true)
        ]).Schema;
        var production = RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            Uuid("enrollmentCodeId", "Tenant-scoped enrollment-code identifier.", true),
            OpaquePlanToken("planToken", "Opaque credential returned by the preview."),
            OpaquePlanToken("idempotencyKey", "Opaque credential returned by the preview.")
        ]).Schema;
        return OneOfRequest("Use the target-owned Development enrollment form or the tenant-scoped preview-confirm form; do not mix them.", development, production);
    }

    private static RequestDefinition OnboardingCreateRequest()
    {
        var development = RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive Development tenant identifier."),
            Uuid("agentId", "Persisted Development V2 agent identifier.", true),
            RequiredInteger("validForMinutes", "Single-use enrollment-code lifetime from 5 through 10 minutes.", 5, 10),
            RequiredPatternString("marker", "Development-only MCP-QA campaign marker note used to own and later revoke the enrollment code.", "^MCP-QA-[A-Za-z0-9_-]{1,121}$", 128)
        ]).Schema;
        var production = RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
            RequiredEnum("runtime", "installer platform runtime.", "linux-x64", "win-x64"),
            RequiredInteger("validForMinutes", "enrollment-code lifetime from 5 through 10 minutes, further bounded by tenant policy.", 5, 10),
            RequiredInteger("maxUses", "enrollment-code maximum uses from 1 through 10, further bounded by tenant policy.", 1, 10),
            OpaquePlanToken("planToken", "Opaque credential returned by the preview."),
            OpaquePlanToken("idempotencyKey", "Opaque credential returned by the preview.")
        ]).Schema;
        return OneOfRequest("Use the target-owned Development QA form or the tenant-scoped preview-confirm form; do not mix them.", development, production);
    }

    private static RequestDefinition OnboardingListRequest() => RequiredRequest([
        PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
        Enum("status", "Optional exact enrollment state.", "active", "expired", "revoked", "all"),
        NonNegativeLongIdentifier("cursor", "Optional non-negative enrollment page cursor."),
        Integer("limit", "Optional enrollment page limit from 1 through 100.", 1, 100)
    ]);

    private static RequestDefinition OneOfRequest(string description, params JsonObject[] alternatives) => new(new JsonObject
    {
        ["description"] = description,
        ["oneOf"] = new JsonArray(alternatives)
    }, true);

    private static RequestDefinition ClientTelemetryRequest()
    {
        var legacyIdentity = RequiredRequest([RequiredString("clientIdentity", "Legacy client identity for the compatibility telemetry cache.")]).Schema;
        var developmentTarget = RequiredRequest([
            PositiveIntIdentifier("tenantId", "Positive Development tenant identifier."),
            Uuid("agentId", "Persisted V2 agent identifier for the target-gated telemetry snapshot.", true)
        ]).Schema;

        return new RequestDefinition(new JsonObject
        {
            ["description"] = "Provide either the legacy clientIdentity or the recommended persisted V2 tenantId and agentId pair; do not mix both forms.",
            ["oneOf"] = new JsonArray(legacyIdentity, developmentTarget)
        }, true);
    }

    private static RequestDefinition ProductionClientTargetRequest() => RequiredRequest([
        PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
        Uuid("agentId", "Persisted V2 agent identifier.", true)
    ]);

    private static RequestDefinition ProductionClientPingConfirmRequest()
    {
        var request = ProductionClientTargetRequest();
        var properties = request.Schema["properties"]!.AsObject();
        properties["planToken"] = new JsonObject { ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 128, ["pattern"] = "^[A-Za-z0-9_-]{32,128}$", ["description"] = "Opaque credential returned by preview_ping." };
        properties["idempotencyKey"] = new JsonObject { ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 128, ["pattern"] = "^[A-Za-z0-9_-]{32,128}$", ["description"] = "Opaque credential returned by preview_ping." };
        request.Schema["required"]!.AsArray().Add("planToken");
        request.Schema["required"]!.AsArray().Add("idempotencyKey");
        return request;
    }

    private static RequestDefinition ProductionClientSoftwareUpdateConfirmRequest()
    {
        var request = ProductionClientTargetRequest();
        var properties = request.Schema["properties"]!.AsObject();
        properties["planToken"] = new JsonObject { ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 128, ["pattern"] = "^[A-Za-z0-9_-]{32,128}$", ["description"] = "Opaque credential returned by preview_software_update." };
        properties["idempotencyKey"] = new JsonObject { ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 128, ["pattern"] = "^[A-Za-z0-9_-]{32,128}$", ["description"] = "Opaque credential returned by preview_software_update." };
        request.Schema["required"]!.AsArray().Add("planToken");
        request.Schema["required"]!.AsArray().Add("idempotencyKey");
        return request;
    }

    private static RequestDefinition ProductionConnectivityTestConfirmRequest() => RequiredRequest([
        OpaquePlanToken("planToken", "Opaque credential returned by preview_test."),
        OpaquePlanToken("idempotencyKey", "Opaque credential returned by preview_test.")
    ]);

    private static RequestDefinition ProductionEventConfirmRequest() => RequiredRequest([
        Uuid("eventId", "Persisted event UUID.", true),
        OpaquePlanToken("planToken", "Opaque credential returned by the matching event preview."),
        OpaquePlanToken("idempotencyKey", "Opaque credential returned by the matching event preview.")
    ]);

    private static RequestDefinition ProductionClientDisablePreviewRequest() => RequiredRequest([
        PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
        Uuid("agentId", "Persisted V2 agent identifier.", true),
        RequiredString("reason", "Bounded non-secret reason for disabling this client.", 256)
    ]);

    private static RequestDefinition ProductionClientDisableConfirmRequest()
    {
        var request = ProductionClientDisablePreviewRequest();
        var properties = request.Schema["properties"]!.AsObject();
        properties["planToken"] = new JsonObject { ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 128, ["pattern"] = "^[A-Za-z0-9_-]{32,128}$", ["description"] = "Opaque credential returned by preview_disable." };
        properties["idempotencyKey"] = new JsonObject { ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 128, ["pattern"] = "^[A-Za-z0-9_-]{32,128}$", ["description"] = "Opaque credential returned by preview_disable." };
        request.Schema["required"]!.AsArray().Add("planToken");
        request.Schema["required"]!.AsArray().Add("idempotencyKey");
        return request;
    }

    private static RequestDefinition ProductionClientDeletePreviewRequest() => RequiredRequest([
        PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
        Uuid("agentId", "Persisted V2 agent identifier.", true),
        RequiredString("reason", "Bounded non-secret reason for irreversible client decommissioning.", 256)
    ]);

    private static RequestDefinition ProductionClientDeleteConfirmRequest()
    {
        var request = ProductionClientDeletePreviewRequest();
        var properties = request.Schema["properties"]!.AsObject();
        properties["planToken"] = new JsonObject { ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 128, ["pattern"] = "^[A-Za-z0-9_-]{32,128}$", ["description"] = "Opaque credential returned by preview_delete." };
        properties["idempotencyKey"] = new JsonObject { ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 128, ["pattern"] = "^[A-Za-z0-9_-]{32,128}$", ["description"] = "Opaque credential returned by preview_delete." };
        request.Schema["required"]!.AsArray().Add("planToken");
        request.Schema["required"]!.AsArray().Add("idempotencyKey");
        return request;
    }

    private static RequestDefinition ProductionClientEnableConfirmRequest()
    {
        var request = ProductionClientTargetRequest();
        var properties = request.Schema["properties"]!.AsObject();
        properties["planToken"] = new JsonObject { ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 128, ["pattern"] = "^[A-Za-z0-9_-]{32,128}$", ["description"] = "Opaque credential returned by preview_enable." };
        properties["idempotencyKey"] = new JsonObject { ["type"] = "string", ["minLength"] = 32, ["maxLength"] = 128, ["pattern"] = "^[A-Za-z0-9_-]{32,128}$", ["description"] = "Opaque credential returned by preview_enable." };
        request.Schema["required"]!.AsArray().Add("planToken");
        request.Schema["required"]!.AsArray().Add("idempotencyKey");
        return request;
    }

    private static JsonObject PolicyDraftSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 160 },
            ["environment"] = IntegerEnum(1, 2, "1 is Development; 2 is Production."),
            ["effect"] = IntegerEnum(1, 2, "1 is Deny; 2 is Allow."),
            ["priority"] = new JsonObject { ["type"] = "integer", ["minimum"] = -10_000, ["maximum"] = 10_000 },
            ["principalSelector"] = PrincipalSelectorSchema(),
            ["targetSelector"] = TargetSelectorSchema(),
            ["targetClassification"] = IntegerEnum(0, 5, "Optional target classification enum value."),
            ["operationFamily"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 524_287, ["description"] = "One or more known McpOperatorOperationFamily flag values." },
            ["operation"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 },
            ["constraints"] = PolicyConstraintsSchema(),
            ["expiresAtUtc"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            ["reviewByUtc"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            ["auditReference"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 }
        },
        ["required"] = new JsonArray("name", "environment", "effect", "priority", "principalSelector", "targetSelector", "operationFamily", "constraints")
    };

    private static JsonObject PrincipalSelectorSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["kind"] = IntegerEnum(1, 5, "Principal selector kind enum value."),
            ["value"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 }
        },
        ["required"] = new JsonArray("kind", "value")
    };

    private static JsonObject TargetSelectorSchema() => new()
    {
        ["description"] = "ExactAgent (1), Tenant (2), ClientTag (3), ControlPlane (4), or DevelopmentEnvironment (5). Kinds 4 and 5 use tenantId 0 without agentId or clientTag. Kind 5 covers all current and future Dev targets and requires environment Development (1).",
        ["oneOf"] = new JsonArray(
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["kind"] = new JsonObject { ["const"] = 1 }, ["tenantId"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = int.MaxValue }, ["agentId"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" } },
                ["required"] = new JsonArray("kind", "tenantId", "agentId")
            },
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["kind"] = new JsonObject { ["const"] = 2 }, ["tenantId"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = int.MaxValue } },
                ["required"] = new JsonArray("kind", "tenantId")
            },
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["kind"] = new JsonObject { ["const"] = 3 }, ["tenantId"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = int.MaxValue }, ["clientTag"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 } },
                ["required"] = new JsonArray("kind", "tenantId", "clientTag")
            },
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["kind"] = new JsonObject { ["const"] = 4 }, ["tenantId"] = new JsonObject { ["const"] = 0 } },
                ["required"] = new JsonArray("kind", "tenantId")
            },
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["kind"] = new JsonObject { ["const"] = 5 }, ["tenantId"] = new JsonObject { ["const"] = 0 } },
                ["required"] = new JsonArray("kind", "tenantId")
            })
    };

    private static JsonObject PolicyConstraintsSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["readRoots"] = BoundedStringArray(4_096),
            ["writeRoots"] = BoundedStringArray(4_096),
            ["allowedShells"] = BoundedStringArray(64),
            ["workingDirectories"] = BoundedStringArray(4_096),
            ["maxCommandDurationSeconds"] = PositiveInteger(),
            ["maxTerminalIdleSeconds"] = PositiveInteger(),
            ["maxTerminalLifetimeSeconds"] = PositiveInteger(),
            ["maxConcurrentTerminalSessions"] = PositiveInteger(),
            ["maxConcurrentCommands"] = PositiveInteger(),
            ["maxOutputBytes"] = PositiveInteger(),
            ["maxArtifactBytes"] = PositiveInteger(),
            ["maxScriptBytes"] = PositiveInteger(),
            ["maxJobTargetCount"] = PositiveInteger(),
            ["maxTaskTargetCount"] = PositiveInteger(),
            ["maxFanOut"] = PositiveInteger(),
            ["allowedTargetClassifications"] = new JsonObject { ["type"] = "array", ["maxItems"] = 64, ["uniqueItems"] = true, ["items"] = IntegerEnum(0, 5, "Target classification enum value.") },
            ["maxOnboardingCodeLifetimeSeconds"] = PositiveInteger(),
            ["maxOnboardingCodeUses"] = PositiveInteger(),
            ["requiredConfirmationClass"] = IntegerEnum(0, 6, "Confirmation class enum value."),
            ["destructiveOperationsAllowed"] = new JsonObject { ["type"] = "boolean" },
            ["allowActiveTenantDeletion"] = new JsonObject { ["type"] = "boolean", ["description"] = "Required to delete the caller's active tenant through the ControlPlane tenant lifecycle." }
        }
    };

    private static JsonObject BoundedStringArray(int maximumLength) => new()
    {
        ["type"] = "array",
        ["maxItems"] = 64,
        ["uniqueItems"] = true,
        ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maximumLength }
    };

    private static JsonObject PositiveInteger() => new()
    {
        ["type"] = "integer",
        ["minimum"] = 1,
        ["maximum"] = int.MaxValue
    };

    private static JsonObject IntegerEnum(int minimum, int maximum, string description) => new()
    {
        ["type"] = "integer",
        ["minimum"] = minimum,
        ["maximum"] = maximum,
        ["description"] = description
    };

    private static RequestDefinition CreateRequest(IEnumerable<(string Name, JsonObject Schema, bool Required)> fields, bool required)
    {
        var properties = new JsonObject();
        var requiredFields = new JsonArray();
        foreach (var (name, schema, isRequired) in fields)
        {
            properties[name] = schema;
            if (isRequired) requiredFields.Add(name);
        }

        return new RequestDefinition(new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = properties,
            ["required"] = requiredFields
        }, required);
    }

    private static (string Name, JsonObject Schema, bool Required) RequiredString(string name, string description, int maxLength = 200)
        => (name, new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maxLength, ["description"] = description }, true);

    private static (string Name, JsonObject Schema, bool Required) RequiredPatternString(string name, string description, string pattern, int maxLength)
        => (name, new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maxLength, ["pattern"] = pattern, ["description"] = description }, true);

    private static (string Name, JsonObject Schema, bool Required) String(string name, string description, int maxLength = 200)
        => (name, new JsonObject { ["type"] = "string", ["maxLength"] = maxLength, ["description"] = description }, false);

    private static (string Name, JsonObject Schema, bool Required) PatternString(string name, string description, string pattern, int maxLength)
        => (name, new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maxLength, ["pattern"] = pattern, ["description"] = description }, false);

    private static (string Name, JsonObject Schema, bool Required) RequiredEnum(string name, string description, params string[] values)
        => (name, new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => JsonValue.Create(value)).ToArray()), ["description"] = description }, true);

    private static (string Name, JsonObject Schema, bool Required) Boolean(string name, string description)
        => (name, new JsonObject { ["type"] = "boolean", ["description"] = description }, false);

    private static (string Name, JsonObject Schema, bool Required) RequiredInteger(string name, string description, int minimum, int maximum)
        => (name, new JsonObject { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum, ["description"] = description }, true);

    private static (string Name, JsonObject Schema, bool Required) RequiredTrueBoolean(string name, string description)
        => (name, new JsonObject { ["type"] = "boolean", ["const"] = true, ["description"] = description }, true);

    private static (string Name, JsonObject Schema, bool Required) Uuid(string name, string description, bool required)
        => (name, new JsonObject { ["type"] = "string", ["format"] = "uuid", ["description"] = description }, required);

    private static (string Name, JsonObject Schema, bool Required) RequiredIdentifier(string name, string description)
        => (name, new JsonObject
        {
            ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200 },
                new JsonObject { ["type"] = "integer", ["minimum"] = 0 }),
            ["description"] = description
        }, true);

    private static (string Name, JsonObject Schema, bool Required) PositiveIdentifier(string name, string description)
        => (name, new JsonObject
        {
            ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "string", ["pattern"] = "^[1-9][0-9]{0,19}$", ["maxLength"] = 20 },
                new JsonObject { ["type"] = "integer", ["minimum"] = 1 }),
            ["description"] = description
        }, true);

    private static (string Name, JsonObject Schema, bool Required) PositiveIntIdentifier(string name, string description)
        => (name, new JsonObject
        {
            ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "string", ["pattern"] = "^[1-9][0-9]{0,9}$", ["maxLength"] = 10 },
                new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = int.MaxValue }),
            ["description"] = description
        }, true);

    private static (string Name, JsonObject Schema, bool Required) PositiveLongIdentifier(string name, string description)
        => (name, new JsonObject
        {
            ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "string", ["pattern"] = "^[1-9][0-9]{0,18}$", ["maxLength"] = 19 },
                new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = long.MaxValue }),
            ["description"] = description
        }, true);

    private static (string Name, JsonObject Schema, bool Required) NonNegativeIntegerIdentifier(string name, string description)
        => (name, new JsonObject
        {
            ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "string", ["pattern"] = "^(0|[1-9][0-9]{0,9})$", ["maxLength"] = 10 },
                new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = int.MaxValue }),
            ["description"] = description
        }, true);

    private static (string Name, JsonObject Schema, bool Required) NonNegativeLongIdentifier(string name, string description)
        => (name, new JsonObject
        {
            ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "string", ["pattern"] = "^(0|[1-9][0-9]{0,18})$", ["maxLength"] = 19 },
                new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = long.MaxValue }),
            ["description"] = description
        }, false);

    private static (string Name, JsonObject Schema, bool Required) OpaquePlanToken(string name, string description)
        => (name, new JsonObject
        {
            ["type"] = "string",
            ["pattern"] = "^[A-Za-z0-9_-]{32,128}$",
            ["maxLength"] = 128,
            ["description"] = description
        }, false);

    private static RequestDefinition CanonicalScriptRequest(string action)
    {
        var fields = new List<(string Name, JsonObject Schema, bool Required)>
        {
            PositiveIntIdentifier("tenantId", "Admitted Development tenant context; canonical scripts are environment-global."),
            Uuid("agentId", "Persisted agent for signed admission.", true)
        };
        if (action is "create" or "update")
            fields.Add(("source", RequiredRequest([
                RequiredString("name", "Canonical UI library name.", 120),
                String("folderPath", "Canonical library folder, default /.", 512),
                String("description", "Canonical description.", 4096),
                ("content", new JsonObject { ["type"] = "string", ["maxLength"] = 64 * 1024, ["description"] = "Canonical source text, at most 64 KiB UTF-8." }, true),
                RequiredString("scriptType", "Canonical UI script type; storing source does not certify execution eligibility.", 64),
                String("manifestJson", "Optional bounded JSON object manifest.", 16 * 1024)
            ]).Schema, true));
        if (action != "create")
        {
            fields.Add(PositiveLongIdentifier("scriptId", "Existing canonical script ID."));
            fields.Add(PositiveLongIdentifier("expectedSourceRevision", "Observed sourceRevision from canonical get; not reviewedVersion."));
            fields.Add(("expectedContentHash", PatternString("expectedContentHash", "Observed current contentHash from canonical get.", "^[A-Fa-f0-9]{64}$", 64).Schema, true));
        }
        if (action == "parse_manifest") fields.Add(RequiredString("manifestJson", "Replacement JSON object manifest.", 16 * 1024));
        fields.Add(OpaquePlanToken("planToken", "Required on confirm: token from unchanged source preview."));
        fields.Add(OpaquePlanToken("idempotencyKey", "Required on confirm: key from unchanged source preview."));
        return RequiredRequest(fields);
    }

    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> ProductionScriptDraftFields(
        bool includeTarget = true,
        bool includePlanCredentials = false)
    {
        var fields = new List<(string Name, JsonObject Schema, bool Required)>();
        if (includeTarget)
        {
            fields.Add(PositiveIntIdentifier("tenantId", "Positive tenant identifier."));
            fields.Add(Uuid("agentId", "Persisted V2 agent identifier.", true));
        }
        fields.Add(RequiredString("name", "Bounded script name.", 120));
        fields.Add(RequiredString("description", "Bounded reviewed script description.", 512));
        fields.Add(RequiredEnum("shellType", "Policy-allowlisted script runtime.", "sh", "bash", "powershell", "pwsh"));
        fields.Add(RequiredString("content", "UTF-8 script content, at most 65536 bytes. It is not retained in policy or audit records.", 64 * 1024));
        fields.Add(RequiredPatternString("contentHash", "Required SHA-256 of the exact UTF-8 content.", "^[A-Fa-f0-9]{64}$", 64));
        fields.Add(("parameters", new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = 0,
            ["maxItems"] = 32,
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Za-z_][A-Za-z0-9_]{0,63}$" },
                    ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("string", "integer", "boolean", "choice", "secret_reference") },
                    ["required"] = new JsonObject { ["type"] = "boolean" },
                    ["description"] = new JsonObject { ["type"] = "string", ["maxLength"] = 256 },
                    ["defaultValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 1024 },
                    ["options"] = new JsonObject { ["type"] = "array", ["maxItems"] = 32, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128 } },
                    ["secretReference"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Za-z_][A-Za-z0-9_]{0,127}$" }
                },
                ["required"] = new JsonArray("name", "type", "required")
            },
            ["description"] = "Typed parameter schema. secret_reference accepts only a named reference and cannot have a default value."
        }, true));
        fields.Add(RequiredInteger("timeoutSeconds", "Policy-bounded script timeout from 1 through 3600 seconds.", 1, 60 * 60));
        fields.Add(RequiredString("workingDirectory", "Exact policy-allowlisted working directory.", 4096));
        fields.Add(("declaredSideEffects", new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = 1,
            ["maxItems"] = 5,
            ["uniqueItems"] = true,
            ["items"] = new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(1, 2, 3, 4, 5) },
            ["description"] = "Required reviewed declarations: 1 read_only, 2 filesystem_write, 3 service_control, 4 network_access, 5 process_execution."
        }, true));
        fields.Add(String("manifestJson", "Optional bounded JSON object manifest; it is canonicalized and validated by the API.", 16 * 1024));
        if (includePlanCredentials)
        {
            fields.Add(OpaquePlanToken("planToken", "Required only when confirm is true: opaque token from the unchanged preview."));
            fields.Add(OpaquePlanToken("idempotencyKey", "Required only when confirm is true: opaque key from the unchanged preview."));
        }
        return fields;
    }

    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> TenantMutationFields(
        string operation,
        bool includeIdentity)
    {
        var fields = new List<(string Name, JsonObject Schema, bool Required)>();
        if (includeIdentity)
        {
            fields.Add(PositiveIntIdentifier("tenantId", "Tenant identifier to replace."));
            fields.Add(PositiveLongIdentifier("expectedVersion", "Current tenant ETag version; stale writes are rejected."));
        }

        fields.Add(RequiredString("name", "Tenant display name.", 160));
        fields.Add(String("description", "Optional bounded tenant description.", 4_096));
        fields.Add(String("location", "Optional bounded tenant location.", 256));
        fields.Add(("domains", StringArray("domains", "Tenant domains. Entries are unique case-insensitively.", 0, 64, 253).Schema, true));
        fields.Add(String("contactPerson", "Optional bounded tenant contact name.", 256));
        fields.Add(String("contactEmail", "Optional bounded tenant contact email.", 320));
        fields.Add(("autoUpdate", Boolean("autoUpdate", "Whether clients may automatically update.").Schema, true));
        fields.Add(("autoUpdateChannel", Enum("autoUpdateChannel", "Optional automatic-update channel; defaults to stable.", "stable", "prerelease").Schema, false));
        fields.Add(String("autoUpdateTargetVersion", "Optional bounded automatic-update target version.", 128));
        fields.Add(("planToken", OpaquePlanToken("planToken", $"Opaque token returned by the matching tenant {operation} preview.").Schema, false));
        fields.Add(("idempotencyKey", OpaquePlanToken("idempotencyKey", $"Opaque key returned by the matching tenant {operation} preview.").Schema, false));
        return fields;
    }

    private static (string Name, JsonObject Schema, bool Required) RequiredStringArray(string name, string description, int minimum, int maximum)
        => (name, new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = minimum,
            ["maxItems"] = maximum,
            ["uniqueItems"] = true,
            ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200 },
            ["description"] = description
        }, true);

    private static (string Name, JsonObject Schema, bool Required) StringArray(string name, string description, int minimum, int maximum, int maximumItemLength)
        => (name, new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = minimum,
            ["maxItems"] = maximum,
            ["uniqueItems"] = true,
            ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maximumItemLength },
            ["description"] = description
        }, false);

    private static JsonObject NullableStringArraySchema()
        => new()
        {
            ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "null" },
                new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } })
        };

    private static (string Name, JsonObject Schema, bool Required) Integer(string name, string description, int minimum, int maximum)
        => (name, new JsonObject { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum, ["description"] = description }, false);

    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> JobRunListFields()
        =>
        [
            ("status", Enum("status", "Optional run status.", "Pending", "Running", "Succeeded", "Failed", "Cancelled", "TimedOut").Schema, false),
            ("jobId", PositiveIdentifier("jobId", "Optional positive job identifier.").Schema, false),
            ("tenantId", Integer("tenantId", "Optional positive tenant identifier.", 1, int.MaxValue).Schema, false),
            ("take", Integer("take", "Optional bounded result limit from 1 through 100.", 1, 100).Schema, false)
        ];

    private static RequestDefinition ProductionJobTargetRequest() => RequiredRequest([
        PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
        Uuid("agentId", "Persisted V2 agent identifier.", true)
    ]);

    private static RequestDefinition ProductionJobReferenceRequest() => RequiredRequest([
        .. ProductionJobTargetFields(),
        PositiveLongIdentifier("jobId", "Positive caller-owned job identifier.")
    ]);

    private static RequestDefinition ProductionJobRunReferenceRequest() => RequiredRequest([
        .. ProductionJobTargetFields(),
        PositiveIdentifier("jobRunId", "Positive caller-owned job-run identifier. Returned IDs may be decimal strings.")
    ]);

    private static RequestDefinition ProductionJobRunListRequest() => Request([
        .. ProductionJobTargetFields(),
        ("jobId", PositiveLongIdentifier("jobId", "Optional caller-owned job identifier. Returned IDs are decimal strings.").Schema, false)
    ]);

    private static RequestDefinition ProductionJobMutationRequest(string operation)
    {
        var fields = new List<(string Name, JsonObject Schema, bool Required)>(ProductionJobTargetFields());
        if (operation != "create")
        {
            fields.Add(PositiveLongIdentifier("jobId", "Positive caller-owned job identifier."));
            fields.Add(PositiveLongIdentifier("expectedVersion", "Current positive job ETag version; stale writes are rejected."));
        }
        switch (operation)
        {
            case "create":
            case "update":
                fields.Add(("job", ProductionJobDraftSchema(), true));
                break;
            case "param_add":
                fields.Add(("parameter", ProductionJobParameterSchema(), true));
                break;
            case "param_update":
                fields.Add(PositiveLongIdentifier("parameterId", "Existing positive caller-owned job parameter identifier."));
                fields.Add(("parameter", ProductionJobParameterSchema(), true));
                break;
            case "param_delete":
                fields.Add(PositiveLongIdentifier("parameterId", "Existing positive caller-owned job parameter identifier."));
                break;
            case "step_add":
                fields.Add(("step", ProductionJobStepSchema(), true));
                break;
            case "step_update":
                fields.Add(PositiveLongIdentifier("stepId", "Existing positive caller-owned job step identifier."));
                fields.Add(("step", ProductionJobStepSchema(), true));
                break;
            case "step_reorder":
                fields.Add(PositiveLongIdentifier("stepId", "Existing positive caller-owned job step identifier."));
                fields.Add(PositiveIntIdentifier("ordinal", "New positive step ordinal."));
                break;
            case "step_delete":
                fields.Add(PositiveLongIdentifier("stepId", "Existing positive caller-owned job step identifier."));
                break;
        }
        fields.Add(OpaquePlanToken("planToken", "Opaque credential returned by the matching job preview."));
        fields.Add(OpaquePlanToken("idempotencyKey", "Opaque credential returned by the matching job preview."));
        return RequiredRequest(fields);
    }

    private static RequestDefinition ProductionJobRunStartRequest() => RequiredRequest([
        .. ProductionJobTargetFields(),
        PositiveLongIdentifier("jobId", "Positive caller-owned job identifier."),
        ("inputs", new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string", ["maxLength"] = 1024 }, ["description"] = "Optional typed job inputs. Secret parameters accept opaque reference names only." }, false),
        OpaquePlanToken("planToken", "Opaque credential returned by the start preview."),
        OpaquePlanToken("idempotencyKey", "Opaque credential returned by the start preview.")
    ]);

    private static RequestDefinition ProductionJobRunActionRequest() => RequiredRequest([
        .. ProductionJobTargetFields(),
        PositiveIdentifier("jobRunId", "Positive caller-owned job-run identifier."),
        String("reason", "Optional bounded cancellation reason.", 128),
        OpaquePlanToken("planToken", "Opaque credential returned by the run-action preview."),
        OpaquePlanToken("idempotencyKey", "Opaque credential returned by the run-action preview.")
    ]);

    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> ProductionJobTargetFields() => [
        PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
        Uuid("agentId", "Persisted V2 agent identifier.", true)
    ];

    private static RequestDefinition ProductionTaskListRequest() => RequiredRequest([
        .. ProductionTaskTargetFields(),
        ("state", Enum("state", "Optional exact owned-task state.", "Pending", "Processing", "CancelRequested", "Completed", "Failed", "Cancelled").Schema, false),
        String("sinceUtc", "Optional ISO-8601 lower created-at bound.", 64),
        Integer("limit", "Optional bounded task limit from 1 through 100.", 1, 100)
    ]);

    private static RequestDefinition ProductionTaskReferenceRequest() => RequiredRequest([
        .. ProductionTaskTargetFields(),
        PositiveLongIdentifier("taskId", "Positive caller-owned task identifier. Returned IDs are decimal strings.")
    ]);

    private static RequestDefinition ProductionTaskLogsRequest() => RequiredRequest([
        .. ProductionTaskTargetFields(),
        PositiveLongIdentifier("taskId", "Positive caller-owned task identifier. Returned IDs are decimal strings."),
        NonNegativeLongIdentifier("sinceId", "Optional exclusive task-log cursor."),
        Stream("stream"),
        Integer("limit", "Optional bounded log limit from 1 through 100.", 1, 100)
    ]);

    private static RequestDefinition ProductionTaskLogsByRequestRequest() => RequiredRequest([
        .. ProductionTaskTargetFields(),
        RequiredPatternString("requestId", "Exact caller-owned command request identifier.", "^[A-Fa-f0-9]{32}$", 32),
        NonNegativeLongIdentifier("sinceId", "Optional exclusive task-log cursor."),
        Stream("stream"),
        Integer("limit", "Optional bounded log limit from 1 through 100.", 1, 100)
    ]);

    private static RequestDefinition ProductionTaskMutationRequest(string operation)
    {
        var fields = new List<(string Name, JsonObject Schema, bool Required)>(ProductionTaskTargetFields());
        switch (operation)
        {
            case "create_command":
                fields.Add(("command", ProductionTaskCommandSchema(), true));
                break;
            case "run_library_script":
                fields.Add(("script", ProductionTaskScriptSchema(), true));
                break;
            case "cancel":
                fields.Add(PositiveLongIdentifier("taskId", "Positive caller-owned task identifier. Returned IDs are decimal strings."));
                break;
        }
        fields.Add(OpaquePlanToken("planToken", "Opaque credential returned by the matching task preview."));
        fields.Add(OpaquePlanToken("idempotencyKey", "Opaque credential returned by the matching task preview."));
        return RequiredRequest(fields);
    }

    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> ProductionTaskTargetFields() => [
        PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
        Uuid("agentId", "Persisted V2 agent identifier.", true)
    ];

    private static JsonObject ProductionTaskCommandSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["shell"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 32 },
            ["command"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 32 * 1024 },
            ["workingDirectory"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 4096 },
            ["timeoutSeconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 60 * 60 },
            ["maximumOutputBytes"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 48 * 1024 },
            ["environmentReferences"] = new JsonObject { ["type"] = "array", ["maxItems"] = 32, ["items"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Za-z_][A-Za-z0-9_]*$", ["maxLength"] = 128 }, ["description"] = "Client-local reference names only; values are never accepted." }
        },
        ["required"] = new JsonArray("shell", "command", "workingDirectory", "timeoutSeconds", "maximumOutputBytes")
    };

    private static JsonObject ProductionTaskScriptSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["scriptId"] = PositiveLongIdentifier("scriptId", "Exact caller-owned reviewed script identifier.").Schema,
            ["version"] = PositiveLongIdentifier("version", "Exact current caller-owned script version.").Schema,
            ["contentHash"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Fa-f0-9]{64}$", ["maxLength"] = 64 },
            ["parameters"] = new JsonObject { ["type"] = "object", ["maxProperties"] = 32, ["additionalProperties"] = new JsonObject { ["type"] = "string", ["maxLength"] = 1024 }, ["description"] = "Typed script values. Secret parameters are reference names, never values." }
        },
        ["required"] = new JsonArray("scriptId", "version", "contentHash")
    };

    private static RequestDefinition ProductionRequestListRequest() => RequiredRequest([
        .. ProductionRequestTargetFields(),
        ("state", Enum("state", "Optional exact owned-request state.", "Pending", "Claimed", "Completed", "Failed", "Cancelled").Schema, false),
        ("jobId", PositiveLongIdentifier("jobId", "Optional caller-owned job identifier.").Schema, false),
        String("sinceUtc", "Optional ISO-8601 lower created-at bound.", 64),
        Integer("limit", "Optional bounded request limit from 1 through 100.", 1, 100)
    ]);

    private static RequestDefinition ProductionRequestReferenceRequest() => RequiredRequest([
        .. ProductionRequestTargetFields(),
        PositiveIdentifier("requestId", "Positive caller-owned request identifier. Returned IDs are decimal strings.")
    ]);

    private static RequestDefinition ProductionRequestMutationRequest(string operation)
    {
        var fields = new List<(string Name, JsonObject Schema, bool Required)>(ProductionRequestTargetFields());
        switch (operation)
        {
            case "create":
                fields.Add(PositiveLongIdentifier("jobId", "Exact caller-owned job identifier."));
                fields.Add(("summary", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 4096 }, true));
                break;
            case "update":
                fields.Add(PositiveIdentifier("requestId", "Positive caller-owned request identifier."));
                fields.Add(PositiveLongIdentifier("expectedVersion", "Current ETag revision required for optimistic concurrency."));
                fields.Add(("summary", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 4096 }, true));
                break;
            case "claim":
                fields.Add(PositiveIdentifier("requestId", "Positive caller-owned request identifier."));
                fields.Add(PositiveLongIdentifier("expectedVersion", "Current ETag revision required for optimistic concurrency."));
                fields.Add(("claimReference", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 }, true));
                break;
            case "complete":
            case "fail":
                fields.Add(PositiveIdentifier("requestId", "Positive caller-owned request identifier."));
                fields.Add(PositiveLongIdentifier("expectedVersion", "Current ETag revision required for optimistic concurrency."));
                fields.Add(("resultSummary", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 48 * 1024 }, true));
                break;
            case "cancel":
                fields.Add(PositiveIdentifier("requestId", "Positive caller-owned request identifier."));
                fields.Add(PositiveLongIdentifier("expectedVersion", "Current ETag revision required for optimistic concurrency."));
                fields.Add(("resultSummary", new JsonObject { ["type"] = "string", ["maxLength"] = 48 * 1024 }, false));
                break;
        }
        fields.Add(OpaquePlanToken("planToken", "Opaque credential returned by the matching request preview."));
        fields.Add(OpaquePlanToken("idempotencyKey", "Opaque credential returned by the matching request preview."));
        return RequiredRequest(fields);
    }

    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> ProductionRequestTargetFields() => [
        PositiveIntIdentifier("tenantId", "Positive tenant identifier."),
        Uuid("agentId", "Persisted V2 agent identifier.", true)
    ];

    private static JsonObject ProductionJobDraftSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 120 },
            ["folderPath"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 },
            ["description"] = new JsonObject { ["type"] = "string", ["maxLength"] = 512 },
            ["optionsJson"] = new JsonObject { ["type"] = "string", ["maxLength"] = 8192 }
        },
        ["required"] = new JsonArray("name", "folderPath")
    };

    private static JsonObject ProductionJobParameterSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 64, ["pattern"] = "^[A-Za-z_][A-Za-z0-9_]*$" },
            ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("string", "integer", "boolean", "choice", "secret_reference") },
            ["required"] = new JsonObject { ["type"] = "boolean" },
            ["description"] = new JsonObject { ["type"] = "string", ["maxLength"] = 256 },
            ["defaultValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 1024 },
            ["options"] = new JsonObject { ["type"] = "array", ["maxItems"] = 32, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128 } },
            ["secretReference"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128, ["pattern"] = "^[A-Za-z_][A-Za-z0-9_]*$" }
        },
        ["required"] = new JsonArray("name", "type", "required")
    };

    private static JsonObject ProductionJobStepSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["ordinal"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 64 },
            ["scriptId"] = PositiveLongIdentifier("scriptId", "Exact reviewed library script identifier. Returned IDs are decimal strings.").Schema,
            ["scriptVersion"] = PositiveLongIdentifier("scriptVersion", "Exact reviewed library script revision. Returned revisions are decimal strings.").Schema,
            ["scriptContentHash"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Fa-f0-9]{64}$", ["maxLength"] = 64 },
            ["enabled"] = new JsonObject { ["type"] = "boolean" }
        },
        ["required"] = new JsonArray("scriptId", "scriptVersion", "scriptContentHash")
    };

    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> JobListFields()
        =>
        [
            String("folder", "Optional bounded folder-path prefix.", 512),
            String("search", "Optional bounded job search text.", 512)
        ];

    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> ClientUpdateAttemptFields()
        =>
        [
            ("clientIdentity", new JsonObject { ["type"] = "string", ["format"] = "uuid", ["description"] = "Optional agent identity used by the source API's legacy-named query parameter." }, false),
            Integer("releaseId", "Optional positive update-release identifier.", 1, int.MaxValue),
            ("status", Enum("status", "Optional update-attempt state.", "Claimed", "Downloading", "Staged", "Activating", "GatewayReadmitted", "Accepted", "FailedPreActivation", "RolledBack", "RollbackUnverified").Schema, false)
        ];

    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> JobRunQueryFields()
        =>
        [
            ("status", Enum("status", "Optional run status.", "Pending", "Running", "Succeeded", "Failed", "Cancelled", "TimedOut").Schema, false),
            ("jobId", PositiveIdentifier("jobId", "Optional positive job identifier.").Schema, false),
            ("tenantId", Integer("tenantId", "Optional positive tenant identifier.", 1, int.MaxValue).Schema, false),
            ("agentId", new JsonObject { ["type"] = "string", ["format"] = "uuid", ["description"] = "Optional agent identifier." }, false),
            ("search", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512, ["description"] = "Optional bounded job-run search text." }, false),
            ("page", Integer("page", "Optional zero-based page from 0 through 10000.", 0, 10_000).Schema, false),
            ("pageSize", Integer("pageSize", "Optional page size from 1 through 100.", 1, 100).Schema, false)
        ];

    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> TaskRecentFields()
        =>
        [
            Integer("limit", "Optional bounded result limit from 1 through 100.", 1, 100),
            Integer("tenantId", "Optional positive tenant identifier.", 1, int.MaxValue),
            ("agentId", new JsonObject { ["type"] = "string", ["format"] = "uuid", ["description"] = "Optional persisted V2 agent identifier." }, false),
            String("taskType", "Optional bounded task-type filter.", 128),
            String("status", "Optional bounded task-status filter.", 64)
        ];

    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> TaskLogFields(string identifier, string description)
        =>
        [
            (identifier, identifier == "taskId"
                ? PositiveLongIdentifier(identifier, description).Schema
                : RequiredString(identifier, description).Schema, true),
            ("sinceId", NonNegativeLongIdentifier("sinceId", "Optional exclusive log sequence.").Schema, false),
            Stream("stream")
        ];

    private static (string Name, JsonObject Schema, bool Required) Enum(string name, string description, params string[] values)
        => (name, new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => JsonValue.Create(value)).ToArray()), ["description"] = description }, false);

    private static (string Name, JsonObject Schema, bool Required) Stream(string name)
        => (name, new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("all", "stdout", "stderr"), ["description"] = "Optional log stream." }, false);

    private static string Title(string name)
        => string.Join(' ', name.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    internal static object CreateHandler(IServiceProvider? services, Type type)
        => services is null
            ? Activator.CreateInstance(type) ?? throw new InvalidOperationException($"Could not create MCP handler '{type.FullName}'.")
            : ActivatorUtilities.CreateInstance(services, type);

    private sealed record RequestDefinition(JsonObject Schema, bool Required);
}

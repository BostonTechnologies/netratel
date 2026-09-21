namespace NetRatel.Shared.Operations;

/// <summary>
/// The business target required before an MCP operation may be admitted.
/// This is intentionally independent from OAuth scope: a caller can have the
/// correct scope and still be denied because the operation's target is absent
/// or not within the caller's effective access.
/// </summary>
public enum McpOperationTargetModel
{
    /// <summary>Instance or control-plane operation with no business target.</summary>
    NoBusinessTarget,

    /// <summary>One explicit tenant, without an agent.</summary>
    Tenant,

    /// <summary>
    /// A server-owned object reference. Its tenant and, where applicable,
    /// agent context must be resolved by the receiving authority; callers may
    /// not manufacture either from an object identifier.
    /// </summary>
    ObjectDerived,

    /// <summary>One exact tenant and persisted agent pair.</summary>
    Agent
}

/// <summary>Closed target contract for one advertised MCP operation.</summary>
public sealed record McpOperationTargetDescriptor(
    string ToolName,
    string OperationName,
    McpOperationTargetModel Model);

/// <summary>
/// Single source of truth for target requirements of the checked-in MCP
/// catalog. It deliberately maps every operation, including stdio-only
/// compatibility operations, so transports cannot introduce an implicit
/// "tenant plus agent" default for a legitimate control-plane or object
/// operation.
/// </summary>
public static class McpOperationTargetCatalog
{
    public static IReadOnlyList<McpOperationTargetDescriptor> Operations { get; } =
    [
        .. McpOperationAccessCatalog.Operations.Select(access => new McpOperationTargetDescriptor(
            access.ToolName,
            access.OperationName,
            TargetFor(access.ToolName, access.OperationName)))
    ];

    public static McpOperationTargetDescriptor? Find(string toolName, string? operationName) =>
        string.IsNullOrWhiteSpace(operationName)
            ? null
            : Operations.SingleOrDefault(entry =>
                string.Equals(entry.ToolName, toolName, StringComparison.Ordinal) &&
                string.Equals(entry.OperationName, operationName, StringComparison.Ordinal));

    private static McpOperationTargetModel TargetFor(string tool, string operation) => (tool, operation) switch
    {
        // Discovery and control-plane operations intentionally have no
        // business target. Their distinct instance-scoped permission remains
        // an attenuation boundary for local HTTP credentials.
        ("netratel_auth", "status") or
        ("netratel_health", "get") or
        ("netratel_system", "version") or
        ("netratel_capabilities", "get") or
        ("netratel_access", "whoami") or
        ("netratel_telemetry", "overview") or
        ("netratel_logs", "search") => McpOperationTargetModel.NoBusinessTarget,

        // Policy lifecycle calls contain either an immutable policy reference
        // or a verified target selector. The receiving authority resolves
        // that reference and preserves its binding; it is not a global
        // control-plane fallback.
        ("netratel_policy", _) => McpOperationTargetModel.ObjectDerived,

        // These tools act on server-owned control-plane or caller-owned
        // records. Individual object references are independently validated
        // by their downstream authority, never by an invented agent target.
        ("netratel_tenants", _) or
        ("netratel_notifications", _) or
        ("netratel_connectivity", _) or
        ("netratel_events", _) or
        ("netratel_config", _) or
        ("netratel_search", _) => McpOperationTargetModel.NoBusinessTarget,

        // Pre-enrollment has a real tenant but deliberately no agent: the
        // prospective client does not exist yet.
        ("netratel_onboarding", _) => McpOperationTargetModel.Tenant,

        // These operations take durable, caller-owned references. The API
        // must derive their tenant/agent ownership from persistence before it
        // authorizes a local credential.
        ("netratel_jobs", _) or
        ("netratel_job_runs", _) or
        ("netratel_marker_jobs", _) or
        ("netratel_scripts", _) or
        ("netratel_tasks", _) or
        ("netratel_requests", _) => McpOperationTargetModel.ObjectDerived,

        // Every remaining operational tool has an exact tenant/agent schema.
        ("netratel_access", _) or
        ("netratel_clients", _) or
        ("netratel_files", _) or
        ("netratel_client_logs", _) or
        ("netratel_client_telemetry", _) or
        ("netratel_terminal", _) or
        ("netratel_commands", _) or
        ("netratel_remote_support_v2", _) => McpOperationTargetModel.Agent,

        _ => throw new InvalidOperationException($"MCP operation '{tool}/{operation}' has no target-model classification.")
    };
}

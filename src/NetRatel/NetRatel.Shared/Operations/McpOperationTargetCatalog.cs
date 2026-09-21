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

/// <summary>
/// A durable object identifier whose ownership can be resolved by the API
/// before a locally minted delegation is admitted. The value is never a
/// caller-supplied substitute for the resolved tenant or agent target.
/// </summary>
public enum McpOperationObjectReferenceKind
{
    Job,
    JobRun,
    Task,
    Request
}

/// <summary>Closed target contract for one advertised MCP operation.</summary>
public sealed record McpOperationTargetDescriptor(
    string ToolName,
    string OperationName,
    McpOperationTargetModel Model)
{
    /// <summary>
    /// The closed-schema request property carrying a durable object reference,
    /// when this operation has one. A null value means the operation's target
    /// is represented directly by its catalogued target shape.
    /// </summary>
    public string? ObjectReferencePropertyName { get; init; }

    /// <summary>The persisted object family represented by the request property.</summary>
    public McpOperationObjectReferenceKind? ObjectReferenceKind { get; init; }

    /// <summary>Whether the closed request schema requires the object reference.</summary>
    public bool RequiresObjectReference { get; init; }
}

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
        .. McpOperationAccessCatalog.Operations.Select(access =>
        {
            var objectReference = ObjectReferenceFor(access.ToolName, access.OperationName);
            return new McpOperationTargetDescriptor(
                access.ToolName,
                access.OperationName,
                TargetFor(access.ToolName, access.OperationName))
            {
                ObjectReferencePropertyName = objectReference?.PropertyName,
                ObjectReferenceKind = objectReference?.Kind,
                RequiresObjectReference = objectReference?.Required ?? false
            };
        })
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

    private static (string PropertyName, McpOperationObjectReferenceKind Kind, bool Required)? ObjectReferenceFor(string tool, string operation) => (tool, operation) switch
    {
        ("netratel_jobs", "get" or "details" or "params" or "steps" or "update" or "delete" or
            "param_add" or "param_update" or "param_delete" or "step_add" or "step_update" or "step_reorder" or "step_delete")
            => ("jobId", McpOperationObjectReferenceKind.Job, true),

        ("netratel_job_runs", "get" or "steps" or "logs" or "cancel" or "delete")
            => ("jobRunId", McpOperationObjectReferenceKind.JobRun, true),
        ("netratel_job_runs", "start" or "list" or "query")
            => ("jobId", McpOperationObjectReferenceKind.Job, operation == "start"),

        ("netratel_tasks", "get" or "logs" or "cancel")
            => ("taskId", McpOperationObjectReferenceKind.Task, true),

        ("netratel_requests", "get" or "update" or "claim" or "complete" or "fail" or "cancel")
            => ("requestId", McpOperationObjectReferenceKind.Request, true),
        ("netratel_requests", "create" or "list")
            => ("jobId", McpOperationObjectReferenceKind.Job, operation == "create"),

        _ => null
    };
}

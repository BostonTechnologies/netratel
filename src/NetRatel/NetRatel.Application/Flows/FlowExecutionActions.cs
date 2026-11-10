using System.Text.Json;
using System.Text.Json.Serialization;
using NetRatel.Application.Jobs;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

public sealed record FlowExecutionPinsCapture(FlowGraphDto Graph, string? ProtectedPinsJson);
public sealed record FlowPinnedExecutionDefinition(Guid NodeId, FlowNodeKind Kind, FlowExecutionDefinitionDto Definition,
    FlowExecutionTargetDto? Target, PinnedJobExecutionSnapshot? Job = null, PinnedJobScriptSnapshot? Script = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowJobTargetBindingDto? TargetBinding { get; init; }
};

public interface IFlowExecutionDefinitionPins
{
    Task<FlowExecutionPinsCapture> CaptureAsync(int tenantId, FlowExecutionAuthorityDto authority, Guid versionId,
        FlowGraphDto graph, CancellationToken cancellationToken = default);
    Task<FlowPinnedExecutionDefinition?> ResolveAsync(int tenantId, Guid versionId, Guid nodeId, CancellationToken cancellationToken = default);
}

public sealed class FlowExecutionPinException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

public enum FlowExecutionActionDisposition
{
    Pending = 0, Running = 1, AwaitingApproval = 2, Succeeded = 3, Failed = 4, TimedOut = 5,
    Cancelled = 6, Unknown = 7, Unsupported = 8, Denied = 9
}

/// <summary>Resolved inputs are server-owned and omitted from trace receipts. The durable runtime supplies its exact current lease.</summary>
public sealed record FlowExecutionActionRequest(FlowRunLease Lease, Guid NodeId, Guid ActionKey, int NodeAttempt,
    string? InputsJson = null, FlowExecutionReplayDescriptorDto? OriginalReplay = null)
{
    public int RecoveryAttempt { get; init; }
    public DateTimeOffset? DeadlineAtUtc { get; init; }
    public bool AllowUnstartedDispatch { get; init; } = true;
}
public sealed record FlowExecutionActionReceipt(Guid ActionKey, ExecutionAdmissionKind Kind, string ExecutionId,
    string? SourceExecutionId = null, int RecoveryAttempt = 0)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DefinitionDigest { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TargetPlanDigest { get; init; }
}

public sealed record FlowExecutionActionOutput(FlowExecutionDefinitionDto Definition, FlowExecutionTargetDto Target,
    string ExecutionId, string AttemptId, DateTimeOffset? StartedAtUtc = null, DateTimeOffset? CompletedAtUtc = null,
    int? ExitCode = null, string? Summary = null, string? StandardOutput = null, string? StandardError = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(FlowNullableJsonElementConverter))]
    JsonElement? Data = null, bool OutputAvailable = false, bool Truncated = false,
    IReadOnlyList<FlowExecutionDataReferenceDto>? References = null);
public sealed record FlowExecutionActionResult(FlowExecutionActionDisposition Disposition, string Code,
    FlowExecutionActionReceipt? Receipt = null, FlowExecutionActionOutput? Output = null,
    FlowExecutionEventDto? TerminalEvent = null);

public interface IFlowExecutionActionDispatcher
{
    Task<FlowExecutionActionResult> DispatchAsync(FlowExecutionActionRequest request, CancellationToken cancellationToken = default);
    Task<FlowExecutionActionResult> ObserveAsync(FlowExecutionActionRequest request, FlowExecutionActionReceipt receipt,
        CancellationToken cancellationToken = default);
}

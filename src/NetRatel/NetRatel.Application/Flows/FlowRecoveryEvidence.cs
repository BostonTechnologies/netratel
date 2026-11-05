using System.Text.Json;
using NetRatel.Application.Jobs;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>Projects recorded canonical result metadata. It performs no execution, source lookup or permission grant.</summary>
public static class FlowRecoveryEvidence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<FlowRecoveryAttemptEvidence> FromOutputs(FlowRunLease lease,
        IReadOnlyDictionary<Guid, JsonElement> outputs, IReadOnlyList<Guid> executionOrder)
    {
        if (lease.Event.ExecutionEvent is not { } source) return Array.Empty<FlowRecoveryAttemptEvidence>();
        var graph = lease.Version.Graph;
        if (graph.Nodes.Count > FlowGraphV2Limits.MaximumNodes || outputs.Count > FlowGraphV2Limits.MaximumNodes ||
            executionOrder.Count > FlowGraphV2Limits.MaximumNodes || executionOrder.Distinct().Count() != executionOrder.Count ||
            executionOrder.Any(id => !graph.Nodes.Any(node => node.Id == id)) ||
            graph.Nodes.Any(node => IsExecution(node.Kind) && outputs.ContainsKey(node.Id) && !executionOrder.Contains(node.Id)))
            throw new ArgumentException("Recorded execution evidence or its exact order is invalid.");
        var attempts = new List<FlowRecoveryAttemptEvidence>();
        foreach (var nodeId in executionOrder)
        {
            var node = graph.Nodes.Single(node => node.Id == nodeId);
            if (!IsExecution(node.Kind) ||
                !outputs.TryGetValue(node.Id, out var value)) continue;
            FlowExecutionActionResult? result = null;
            try
            {
                var recorded = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("executionResult", out var nested)
                    ? nested : value;
                if (recorded.ValueKind == JsonValueKind.Object && HasProperty(recorded, "disposition") && HasProperty(recorded, "code"))
                    result = recorded.Deserialize<FlowExecutionActionResult>(Json);
            }
            catch (JsonException) { }
            var state = State(result);
            var receipt = result?.Receipt;
            var terminal = result?.TerminalEvent;
            var acceptedId = receipt?.ExecutionId;
            if (receipt is not null && (receipt.ActionKey == Guid.Empty || !Enum.IsDefined(receipt.Kind) ||
                !FlowGraphValidator.IsBoundedText(receipt.ExecutionId, FlowExecutionEventLimits.MaximumIdentifierLength) ||
                receipt.RecoveryAttempt is < 0 or > FlowExecutionEventLimits.MaximumRecoveryAttempts ||
                node.Kind == FlowNodeKind.RerunOriginalExecution && (receipt.RecoveryAttempt < 1 ||
                    source.Replay is null || receipt.SourceExecutionId != source.Replay.SourceExecutionId) ||
                node.Kind == FlowNodeKind.RunJob && receipt.Kind != ExecutionAdmissionKind.Job ||
                node.Kind == FlowNodeKind.RunLibraryScript && receipt.Kind != ExecutionAdmissionKind.LibraryScript))
            {
                receipt = null; acceptedId = null; terminal = null; state = FlowRecoveryActionState.Unknown;
            }
            if (terminal is not null && (receipt is null || terminal.ExecutionId != receipt.ExecutionId ||
                !FlowExecutionEventValidator.Validate(terminal).Valid || !Native(terminal) ||
                terminal.TenantId != lease.Event.TenantId || terminal.Causation is not { } cause || cause.FlowRunId != lease.RunId ||
                cause.NodeId != node.Id || cause.ParentEventId != source.EventId ||
                cause.RootOccurrenceId != (source.Causation?.RootOccurrenceId ?? source.OccurrenceId.ToString("D")) ||
                terminal.SourceKind != (receipt.Kind == ExecutionAdmissionKind.Job ? FlowExecutionSourceKind.Job : FlowExecutionSourceKind.Script)))
            {
                terminal = null;
                state = FlowRecoveryActionState.Unknown;
            }
            if (receipt is not null && (state is FlowRecoveryActionState.Succeeded or FlowRecoveryActionState.Failed or
                    FlowRecoveryActionState.TimedOut or FlowRecoveryActionState.Cancelled) &&
                (terminal is null || terminal.CompletedAtUtc is null || terminal.Outcome != Outcome(state)))
                state = FlowRecoveryActionState.Unknown;
            if (state == FlowRecoveryActionState.Succeeded && receipt is null)
                state = FlowRecoveryActionState.Unknown;
            attempts.Add(new(node.Id, Math.Max(1, receipt?.RecoveryAttempt ?? 1),
                ActionName(node), state,
                node.Kind == FlowNodeKind.RerunOriginalExecution, acceptedId, terminal));
        }
        return attempts.AsReadOnly();
    }

    private static string ActionName(FlowNodeDto node)
    {
        var name = node.ExecutionAction?.Definition.Name;
        if (string.IsNullOrWhiteSpace(name)) return node.Name;
        name = new string(name.Where(character => !char.IsControl(character)).ToArray());
        if (name.Length == 0) return node.Name;
        if (name.Length <= FlowLimits.MaximumNameLength) return name;
        var cut = FlowLimits.MaximumNameLength - 1;
        if (char.IsHighSurrogate(name[cut - 1])) cut--;
        return name[..cut] + "…";
    }

    private static bool HasProperty(JsonElement value, string name) =>
        value.EnumerateObject().Any(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
    private static bool IsExecution(FlowNodeKind kind) =>
        kind is FlowNodeKind.RunLibraryScript or FlowNodeKind.RunJob or FlowNodeKind.RerunOriginalExecution;

    private static FlowRecoveryActionState State(FlowExecutionActionResult? result) => result is null ||
        !Enum.IsDefined(result.Disposition) || !FlowGraphValidator.IsBoundedText(result.Code, 128)
        ? FlowRecoveryActionState.Unknown : result.Disposition switch
        {
            FlowExecutionActionDisposition.Pending => FlowRecoveryActionState.Pending,
            FlowExecutionActionDisposition.Running => FlowRecoveryActionState.Running,
            FlowExecutionActionDisposition.AwaitingApproval => FlowRecoveryActionState.AwaitingApproval,
            FlowExecutionActionDisposition.Succeeded => FlowRecoveryActionState.Succeeded,
            FlowExecutionActionDisposition.Failed => FlowRecoveryActionState.Failed,
            FlowExecutionActionDisposition.TimedOut => FlowRecoveryActionState.TimedOut,
            FlowExecutionActionDisposition.Cancelled => FlowRecoveryActionState.Cancelled,
            FlowExecutionActionDisposition.Unsupported => FlowRecoveryActionState.Unsupported,
            FlowExecutionActionDisposition.Denied => FlowRecoveryActionState.Denied,
            _ => FlowRecoveryActionState.Unknown
        };

    private static FlowExecutionOutcome Outcome(FlowRecoveryActionState state) => state switch
    {
        FlowRecoveryActionState.Succeeded => FlowExecutionOutcome.Succeeded,
        FlowRecoveryActionState.Failed => FlowExecutionOutcome.Failed,
        FlowRecoveryActionState.TimedOut => FlowExecutionOutcome.TimedOut,
        FlowRecoveryActionState.Cancelled => FlowExecutionOutcome.Cancelled,
        _ => FlowExecutionOutcome.Unknown
    };

    private static bool Native(FlowExecutionEventDto value) => value.SourceKind switch
    {
        FlowExecutionSourceKind.Job => value.Provenance == "job-terminal-ready",
        FlowExecutionSourceKind.Script => value.Provenance == "script-terminal-commit",
        FlowExecutionSourceKind.Request => value.Provenance == "request-state-commit",
        _ => false
    };
}

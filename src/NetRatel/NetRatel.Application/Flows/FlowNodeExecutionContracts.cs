using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

public enum FlowNodeProgress { Prepared = 1, Dispatching = 2, Waiting = 3, AwaitingApproval = 4, Completed = 5, Failed = 6 }
public enum FlowCheckpointWriteDisposition { Stored, Conflict, LeaseLost, Invalid, Denied }

/// <summary>
/// One exclusive token. Canonical execution receipts and typed outputs are data, never executable CLR models.
/// Inputs contain values or authorized reference names; resolved credential values must never enter this record.
/// </summary>
public sealed record FlowNodeCheckpointDto(Guid NodeId, int Revision, int Sequence, FlowNodeProgress Progress,
    string DispatchKey, DateTimeOffset StartedAtUtc, DateTimeOffset DeadlineAtUtc)
{
    public int SchemaVersion { get; init; } = 1;
    public int Attempt { get; init; } = 1;
    public IReadOnlyDictionary<string, JsonElement> Inputs { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyDictionary<Guid, JsonElement> Outputs { get; init; } = new Dictionary<Guid, JsonElement>();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(FlowNullableJsonElementConverter))]
    public JsonElement? Output { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedPort { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SelectedEdgeId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? NextNodeId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowExecutionSourceKind? AcceptedSourceKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AcceptedExecutionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? WaitingActionIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? AcceptedReceipt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowIncidentFieldsDto? Fields { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowAiResultDto? Investigation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowTraceDto? Trace { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<Guid, FlowIncidentFieldsDto>? PreparedActionFields { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowRunStatus? ResultStatus { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResultCode { get; init; }
}

/// <summary>Writes use the existing run transaction, lease/fence and a checkpoint revision CAS.</summary>
public interface IFlowNodeExecutionStore
{
    Task<FlowCheckpointWriteDisposition> SaveNodeCheckpointAsync(FlowRunLease lease,
        int expectedRevision, FlowNodeCheckpointDto checkpoint, CancellationToken cancellationToken = default);
    Task<bool> YieldNodeAsync(FlowRunLease lease, int expectedRevision, CancellationToken cancellationToken = default);
}

public static class FlowNodeCheckpointLimits
{
    public const int MaximumCheckpointBytes = 262144;
    public const int MaximumOutputsBytes = 65536;
}

/// <summary>Pure state-transition checks. Durable acceptance and current source authority are verified by the store.</summary>
public static class FlowNodeCheckpointRules
{
    public static bool Valid(FlowRunLease lease, FlowNodeCheckpointDto? state)
    {
        if (state is null || state.SchemaVersion != 1 || state.Revision <= 0 ||
            state.Sequence is < 1 or > FlowGraphV2Limits.MaximumNodes || !Enum.IsDefined(state.Progress) ||
            state.StartedAtUtc == default || state.DeadlineAtUtc <= state.StartedAtUtc ||
            state.DeadlineAtUtc > (lease.CreatedAtUtc == default ? lease.Event.OccurredAtUtc : lease.CreatedAtUtc) + FlowLimits.MaximumRetryAge ||
            !FlowGraphValidator.IsBoundedText(state.DispatchKey, 200) ||
            state.DispatchKey != FlowActionKeys.Create(lease, state.NodeId) ||
            state.Inputs is null || state.Outputs is null || state.Inputs.Count > FlowGraphV2Limits.MaximumBindings ||
            state.Outputs.Count > FlowGraphV2Limits.MaximumNodes)
            return false;
        if (state.ResultStatus is { } terminalStatus && terminalStatus is not (FlowRunStatus.Succeeded or FlowRunStatus.Failed or
                FlowRunStatus.PartiallySucceeded or FlowRunStatus.DeliveryUnknown or FlowRunStatus.Skipped) ||
            state.ResultCode is { } resultCode && (!FlowGraphValidator.IsBoundedText(resultCode, 128) ||
                !resultCode.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_')) ||
            (state.ResultStatus is null) != (state.ResultCode is null)) return false;
        var node = lease.Version.Graph.Nodes.SingleOrDefault(node => node.Id == state.NodeId);
        if (node is null || state.Attempt < 1 ||
            state.Attempt > (node.Kind == FlowNodeKind.RerunOriginalExecution ? node.Rerun?.MaximumAttempts ?? 1 : 1) ||
            state.Inputs.Any(pair => !FlowGraphValidator.IsBoundedText(pair.Key, 64) || pair.Value.ValueKind == JsonValueKind.Undefined) ||
            state.Outputs.Any(pair => !lease.Version.Graph.Nodes.Any(node => node.Id == pair.Key) || pair.Value.ValueKind == JsonValueKind.Undefined))
            return false;
        if (state.PreparedActionFields is { } preparedFields &&
            (preparedFields.Count > FlowLimits.MaximumActionsPerGroup || preparedFields.Any(pair =>
                (node.Kind == FlowNodeKind.ActionGroup ? node.Actions?.Any(action => action.Id == pair.Key) != true : pair.Key != node.Id) ||
                pair.Value is null || !FlowGraphValidator.IsBoundedText(pair.Value.Title, FlowLimits.MaximumTitleLength) ||
                !FlowGraphValidator.IsBoundedText(pair.Value.Description, FlowLimits.MaximumDescriptionLength, multiline: true) ||
                pair.Value.Priority is < 0 or > 3))) return false;
        if (state.AcceptedSourceKind is { } kind && !Enum.IsDefined(kind) ||
            state.AcceptedExecutionId is not null && !FlowGraphValidator.IsBoundedText(state.AcceptedExecutionId, FlowExecutionEventLimits.MaximumIdentifierLength) ||
            (state.AcceptedSourceKind is null) != (state.AcceptedExecutionId is null) ||
            state.AcceptedReceipt is { ValueKind: not JsonValueKind.Object } ||
            state.AcceptedExecutionId is null && state.AcceptedReceipt is not null || state.Output?.ValueKind == JsonValueKind.Undefined)
            return false;
        if (state.WaitingActionIds is { } waitingIds && (waitingIds.Count is < 1 or > FlowLimits.MaximumActionsPerGroup ||
            waitingIds.Distinct().Count() != waitingIds.Count ||
            waitingIds.Any(id => !FlowGraphValidator.ActionTargets(lease.Version.Graph).Any(action => action.Id == id))))
            return false;
        if (state.Progress is FlowNodeProgress.Waiting or FlowNodeProgress.AwaitingApproval)
        {
            var validWait = node.Kind switch
            {
                FlowNodeKind.RunLibraryScript or FlowNodeKind.RunJob or FlowNodeKind.RerunOriginalExecution =>
                    state.AcceptedExecutionId is not null && state.AcceptedReceipt is not null,
                FlowNodeKind.InvestigateWithAi => state.Investigation?.InvestigationId is { } id && id != Guid.Empty,
                FlowNodeKind.CreateIncident or FlowNodeKind.ActionGroup => state.WaitingActionIds is { Count: > 0 },
                _ => false
            };
            if (!validWait) return false;
        }
        if (state.Progress is FlowNodeProgress.Completed or FlowNodeProgress.Failed)
        {
            if (FlowNodeCatalogue.IsTerminal(node.Kind) || state.Progress == FlowNodeProgress.Failed)
            {
                if (state.SelectedPort is not null || state.SelectedEdgeId is not null || state.NextNodeId is not null) return false;
            }
            else
            {
                var edge = lease.Version.Graph.Edges.SingleOrDefault(edge => edge.SourceNodeId == node.Id && edge.SourcePort == state.SelectedPort);
                if (edge is null || edge.Id != state.SelectedEdgeId || edge.TargetNodeId != state.NextNodeId) return false;
            }
            if (state.Output is { } output &&
                (!state.Outputs.TryGetValue(node.Id, out var recorded) || !JsonElement.DeepEquals(recorded, output))) return false;
        }
        else if (state.SelectedPort is not null || state.SelectedEdgeId is not null || state.NextNodeId is not null || state.Output is not null)
            return false;
        try
        {
            if (Encoding.UTF8.GetByteCount(FlowJsonCanonical.Serialize(state.Inputs)) > FlowLimits.MaximumActionBytes ||
                Encoding.UTF8.GetByteCount(FlowJsonCanonical.Serialize(state.Outputs)) > FlowNodeCheckpointLimits.MaximumOutputsBytes ||
                Encoding.UTF8.GetByteCount(FlowJsonCanonical.Serialize(state)) > FlowNodeCheckpointLimits.MaximumCheckpointBytes)
                return false;
            if (state.Trace is { } trace && (trace.SchemaVersion != 1 || trace.GraphSchemaVersion != 2 ||
                trace.ConfigurationHash != lease.Version.ConfigurationHash || trace.Provenance != FlowTraceProvenance.Actual ||
                trace.Steps is null || trace.Steps.Count > FlowTraceLimits.MaximumSteps || Encoding.UTF8.GetByteCount(FlowJsonCanonical.Serialize(trace)) > FlowTraceLimits.MaximumTraceBytes ||
                trace.Steps.Any(step => step is null || step.Provenance != FlowTraceProvenance.Actual ||
                    !lease.Version.Graph.Nodes.Any(node => node.Id == step.NodeId) ||
                    !FlowGraphValidator.IsBoundedText(step.NodeName, FlowLimits.MaximumNameLength) ||
                    lease.Version.Graph.Nodes.Single(node => node.Id == step.NodeId).Name != step.NodeName ||
                    !FlowGraphValidator.IsBoundedText(step.Outcome, 128) || step.DurationMilliseconds < 0 ||
                    step.DurationMilliseconds > FlowLimits.MaximumRetryAge.Ticks / TimeSpan.TicksPerMillisecond ||
                    !SnapshotValid(step.Input) || !SnapshotValid(step.Output)) ||
                trace.Steps.Sum(step => SnapshotBytes(step.Input) + SnapshotBytes(step.Output)) > FlowTraceLimits.MaximumSnapshotTotalBytes))
                return false;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        { return false; }
        return true;
    }

    public static bool CanAdvance(FlowRunLease lease, FlowNodeCheckpointDto? previous, FlowNodeCheckpointDto next)
    {
        if (!Valid(lease, next)) return false;
        if (previous is null)
            return next.Revision == 1 && next.Sequence == 1 && next.Progress == FlowNodeProgress.Prepared &&
                next.AcceptedReceipt is null && next.AcceptedExecutionId is null && next.AcceptedSourceKind is null && next.Outputs.Count == 0 && next.Trace is null &&
                lease.Version.Graph.Nodes.Any(node => node.Id == next.NodeId && FlowNodeCatalogue.IsTrigger(node.Kind));
        if (next.Revision != previous.Revision + 1) return false;
        foreach (var pair in previous.Outputs)
            if (!next.Outputs.TryGetValue(pair.Key, out var value) || !JsonElement.DeepEquals(value, pair.Value)) return false;
        var priorSteps = previous.Trace?.Steps ?? [];
        var nextSteps = next.Trace?.Steps ?? [];
        if (nextSteps.Count < priorSteps.Count || nextSteps.Count > priorSteps.Count + 1) return false;
        for (var index = 0; index < priorSteps.Count; index++)
            if (!FlowJsonCanonical.Equal(priorSteps[index], nextSteps[index])) return false;
        if (previous.Progress == FlowNodeProgress.Completed)
            return previous.NextNodeId == next.NodeId && next.Sequence == previous.Sequence + 1 &&
                next.Progress == FlowNodeProgress.Prepared && next.Attempt == 1 && next.AcceptedReceipt is null &&
                next.AcceptedExecutionId is null && !previous.Outputs.ContainsKey(next.NodeId) &&
                nextSteps.Count == priorSteps.Count;
        foreach (var pair in previous.PreparedActionFields ?? new Dictionary<Guid, FlowIncidentFieldsDto>())
            if (next.PreparedActionFields?.TryGetValue(pair.Key, out var fields) != true ||
                !FlowJsonCanonical.Equal(pair.Value, fields)) return false;
        if (previous.NodeId != next.NodeId || previous.Sequence != next.Sequence ||
            previous.Attempt != next.Attempt || previous.DispatchKey != next.DispatchKey ||
            previous.StartedAtUtc != next.StartedAtUtc || previous.DeadlineAtUtc != next.DeadlineAtUtc ||
            !FlowJsonCanonical.Equal(previous.Inputs, next.Inputs) ||
            previous.AcceptedExecutionId is not null && (previous.AcceptedSourceKind != next.AcceptedSourceKind ||
                previous.AcceptedExecutionId != next.AcceptedExecutionId ||
                previous.AcceptedReceipt is { } receipt && (next.AcceptedReceipt is not { } nextReceipt || !JsonElement.DeepEquals(receipt, nextReceipt))))
            return false;
        var transition = previous.Progress switch
        {
            FlowNodeProgress.Prepared => next.Progress is FlowNodeProgress.Dispatching or FlowNodeProgress.Completed or FlowNodeProgress.Failed,
            FlowNodeProgress.Dispatching => next.Progress is FlowNodeProgress.Dispatching or FlowNodeProgress.Waiting or FlowNodeProgress.AwaitingApproval or FlowNodeProgress.Completed or FlowNodeProgress.Failed,
            FlowNodeProgress.Waiting or FlowNodeProgress.AwaitingApproval => next.Progress is FlowNodeProgress.Dispatching or FlowNodeProgress.Waiting or FlowNodeProgress.AwaitingApproval or FlowNodeProgress.Completed or FlowNodeProgress.Failed,
            _ => false
        };
        if (!transition) return false;
        if (next.Progress is not (FlowNodeProgress.Completed or FlowNodeProgress.Failed)) return nextSteps.Count == priorSteps.Count;
        if (nextSteps.Count != priorSteps.Count + 1) return false;
        var completed = nextSteps[^1];
        return completed.NodeId == next.NodeId && completed.Sequence == next.Sequence &&
            completed.Attempt == next.Attempt && completed.SelectedPort == next.SelectedPort &&
            completed.EdgeId == next.SelectedEdgeId && completed.NextNodeId == next.NextNodeId;
    }

    private static int SnapshotBytes(FlowTraceSnapshotDto snapshot) =>
        snapshot.Value is { } value ? JsonSerializer.SerializeToUtf8Bytes(value).Length : 0;
    private static bool SnapshotValid(FlowTraceSnapshotDto? snapshot)
    {
        if (snapshot is null || !Enum.IsDefined(snapshot.State) ||
            snapshot.OriginalUtf8Bytes < 0 || snapshot.Excerpt?.Length > FlowTraceLimits.MaximumExcerptLength ||
            snapshot.Reference is not null && !FlowGraphValidator.IsBoundedText(snapshot.Reference, 512))
            return false;
        if (snapshot.State == FlowTraceDataState.Missing)
            return snapshot.Value is null && snapshot.Excerpt is null && snapshot.OriginalUtf8Bytes is null;
        if (snapshot.State == FlowTraceDataState.Truncated)
            return snapshot.Value is null && snapshot.OriginalUtf8Bytes > FlowTraceLimits.MaximumSnapshotBytes &&
                snapshot.Excerpt is not null;
        if (snapshot.Value is not { } value || value.ValueKind == JsonValueKind.Undefined ||
            SnapshotBytes(snapshot) > FlowTraceLimits.MaximumSnapshotBytes)
            return false;
        return snapshot.State == FlowTraceDataState.Null ? value.ValueKind == JsonValueKind.Null :
            snapshot.State == FlowTraceDataState.Empty ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() == "",
                JsonValueKind.Array => value.GetArrayLength() == 0,
                JsonValueKind.Object => !value.EnumerateObject().Any(),
                _ => false
            } : snapshot.State == FlowTraceDataState.Present;
    }

}

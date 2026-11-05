using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

/// <summary>
/// One durable exclusive token controls actual Velox node invocation. Inactive branches are never compiled
/// as predecessors, and an accepted canonical execution always yields until its exact committed result exists.
/// </summary>
public sealed class FlowGraphV2Runtime(IFlowNodeExecutionStore checkpoints, IFlowNodeRuntime velox,
    IServiceScopeFactory scopes, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<FlowRuntimeResult> ExecuteAsync(FlowRunLease lease,
        Func<FlowIncidentActionDraft, CancellationToken, Task<FlowIncidentActionResult>> executeAction,
        CancellationToken ct = default)
    {
        var graph = lease.Version.Graph;
        if (!FlowGraphV2Validator.ValidatePublished(graph).Valid || !FlowContractValidation.ValidEnvelope(lease.Event) ||
            lease.Version.TenantId != lease.Event.TenantId || lease.Version.Id != lease.Event.FlowVersionId ||
            lease.SourceInstanceId == Guid.Empty ||
            FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(graph, Json)) != lease.Version.ConfigurationHash)
            return new(FlowRunStatus.Failed, "runtime-input-invalid");
        var trigger = graph.Nodes.Single(node => FlowNodeCatalogue.IsTrigger(node.Kind));
        if ((trigger.Kind == FlowNodeKind.ExecutionEvent) != (lease.Event.ExecutionEvent is not null))
            return new(FlowRunStatus.Failed, "runtime-source-mismatch");
        var current = lease.Continuation?.Node;
        if (current is not null && !FlowNodeCheckpointRules.Valid(lease, current))
            return new(FlowRunStatus.Failed, "runtime-checkpoint-invalid");
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;

        async Task<bool> SaveAsync(FlowNodeCheckpointDto next)
        {
            var outcome = await checkpoints.SaveNodeCheckpointAsync(lease, current?.Revision ?? 0, next, ct).ConfigureAwait(false);
            if (outcome != FlowCheckpointWriteDisposition.Stored) return false;
            current = next;
            return true;
        }
        FlowRuntimeResult Resume(string code) => new(FlowRunStatus.RetryWaiting, code)
        { Continuation = current is null ? null : new(current.NodeId, current.Fields, current.Investigation) { Node = current } };

        for (var visited = 0; visited <= FlowGraphV2Limits.MaximumNodes; visited++)
        {
            ct.ThrowIfCancellationRequested();
            if (current?.Progress is FlowNodeProgress.Failed ||
                current?.Progress == FlowNodeProgress.Completed && current.NextNodeId is null)
                return new(current.ResultStatus ?? FlowRunStatus.Failed, current.ResultCode ?? "runtime-terminal-invalid");
            var nodeId = current is null ? trigger.Id :
                current.Progress == FlowNodeProgress.Completed ? current.NextNodeId!.Value : current.NodeId;
            var node = graph.Nodes.Single(definition => definition.Id == nodeId);
            string? inputError = null;
            if (current is null || current.Progress == FlowNodeProgress.Completed)
            {
                var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var pair in node.Inputs ?? new Dictionary<string, FlowInputBindingDto>())
                {
                    var resolved = FlowValueEvaluation.Resolve(pair.Value, lease.Event.ExecutionEvent, lease.Event.Data,
                        current?.Outputs ?? new Dictionary<Guid, JsonElement>());
                    if (!resolved.Available || resolved.Value is not { } value)
                    { inputError = pair.Value.Source == FlowInputSource.SecretReference ? "secret-reference-unavailable" : resolved.Code; break; }
                    values.Add(pair.Key, value);
                }
                var started = clock.GetUtcNow();
                var minutes = node.ExecutionAction?.TimeoutMinutes ?? node.Rerun?.TimeoutMinutes ??
                    node.AiSettings?.DeadlineMinutes ?? 60;
                var deadline = started.AddMinutes(minutes);
                var horizon = (lease.CreatedAtUtc == default ? lease.Event.OccurredAtUtc : lease.CreatedAtUtc) + FlowLimits.MaximumRetryAge;
                if (deadline > horizon) deadline = horizon;
                var prepared = new FlowNodeCheckpointDto(node.Id, (current?.Revision ?? 0) + 1,
                    (current?.Sequence ?? 0) + 1, FlowNodeProgress.Prepared, FlowActionKeys.Create(lease, node.Id), started, deadline)
                { Inputs = values, Outputs = current?.Outputs ?? new Dictionary<Guid, JsonElement>(),
                    Fields = current?.Fields, Investigation = current?.Investigation, Trace = current?.Trace };
                if (!await SaveAsync(prepared).ConfigureAwait(false)) return Resume("node-prepare-not-committed");
            }
            var state = current!;
            // Re-evaluate missing inputs after a prepared-node restart; prior outputs and event are immutable.
            if (inputError is null)
                foreach (var pair in node.Inputs ?? new Dictionary<string, FlowInputBindingDto>())
                    if (!state.Inputs.ContainsKey(pair.Key)) { inputError = pair.Value.Source == FlowInputSource.SecretReference ? "secret-reference-unavailable" : "node-input-unavailable"; break; }
            if (inputError is not null)
            {
                var inputFailure = inputError == "secret-reference-unavailable" &&
                    FlowNodeCatalogue.Port(node.Kind, FlowNodeCatalogue.Unsupported, FlowPortDirection.Output) is not null
                    ? Complete(lease, node, state, FlowNodeCatalogue.Unsupported,
                        JsonSerializer.SerializeToElement(new { outcome = "unsupported", code = inputError }, Json), state.Fields)
                    : Fail(lease, node, state, SafeCode(inputError));
                if (!await SaveAsync(inputFailure).ConfigureAwait(false)) return Resume("node-error-not-committed");
                continue;
            }

            if (IsPure(node.Kind))
            {
                var decision = await velox.ExecuteNodeAsync(lease.RunId, node,
                    _ => Task.FromResult(Pure(lease, node, state)), ct).ConfigureAwait(false);
                var completed = decision.Error is { } error ? Fail(lease, node, state, error) :
                    Complete(lease, node, state, decision.Port, decision.Output, decision.Fields,
                        node.Kind == FlowNodeKind.Finish ? FlowRunStatus.Succeeded : null,
                        node.Kind == FlowNodeKind.Finish ? "flow-finished" : null);
                if (!await SaveAsync(completed).ConfigureAwait(false)) return Resume("node-result-not-committed");
                continue;
            }

            if (state.Progress is FlowNodeProgress.Prepared or FlowNodeProgress.Waiting or FlowNodeProgress.AwaitingApproval)
            {
                if (!await SaveAsync(state with { Revision = state.Revision + 1, Progress = FlowNodeProgress.Dispatching,
                    WaitingActionIds = null }).ConfigureAwait(false)) return Resume("node-admission-not-committed");
                state = current!;
            }
            if (node.Kind is FlowNodeKind.RunLibraryScript or FlowNodeKind.RunJob or FlowNodeKind.RerunOriginalExecution)
            {
                var recovery = RecoveryOrdinal(lease, state);
                var request = new FlowExecutionActionRequest(lease, node.Id, FlowActionKeys.CreateExecution(lease, node.Id, state.Attempt),
                    state.Attempt, JsonSerializer.Serialize(state.Inputs, Json), lease.Event.ExecutionEvent?.Replay)
                { RecoveryAttempt = recovery };
                var dispatcher = services.GetRequiredService<IFlowExecutionActionDispatcher>();
                var receipt = state.AcceptedReceipt?.Deserialize<FlowExecutionActionReceipt>(Json);
                var execution = await velox.ExecuteNodeAsync(lease.RunId, node,
                    token => receipt is null ? dispatcher.DispatchAsync(request, token) : dispatcher.ObserveAsync(request, receipt, token),
                    ct).ConfigureAwait(false);
                execution = ConfirmTerminal(lease, node, execution);
                if (execution.Disposition is FlowExecutionActionDisposition.Pending or FlowExecutionActionDisposition.Running or
                    FlowExecutionActionDisposition.AwaitingApproval)
                {
                    if (execution.Receipt is null) return Resume("execution-admission-unconfirmed");
                    if (clock.GetUtcNow() < state.DeadlineAtUtc)
                    {
                        var waiting = state with { Revision = state.Revision + 1,
                            Progress = execution.Disposition == FlowExecutionActionDisposition.AwaitingApproval ? FlowNodeProgress.AwaitingApproval : FlowNodeProgress.Waiting,
                            AcceptedSourceKind = execution.Receipt.Kind == ExecutionAdmissionKind.Job ? FlowExecutionSourceKind.Job : FlowExecutionSourceKind.Script,
                            AcceptedExecutionId = execution.Receipt.ExecutionId,
                            AcceptedReceipt = JsonSerializer.SerializeToElement(execution.Receipt, Json) };
                        if (!await SaveAsync(waiting).ConfigureAwait(false)) return Resume("execution-receipt-not-committed");
                        if (!await checkpoints.YieldNodeAsync(lease, current!.Revision, ct).ConfigureAwait(false))
                            return Resume("execution-yield-not-committed");
                        return new(waiting.Progress == FlowNodeProgress.AwaitingApproval ? FlowRunStatus.AwaitingApproval : FlowRunStatus.WaitingForExecution,
                            execution.Code) { Continuation = new(waiting.NodeId, waiting.Fields, waiting.Investigation) { Node = waiting } };
                    }
                    // A Flow wait deadline is not authoritative proof that the native execution timed out.
                    execution = new(FlowExecutionActionDisposition.Unknown, "execution-result-wait-expired", execution.Receipt);
                }
                var accepted = execution.Receipt;
                var resultState = state with
                {
                    AcceptedSourceKind = accepted is null ? state.AcceptedSourceKind :
                        accepted.Kind == ExecutionAdmissionKind.Job ? FlowExecutionSourceKind.Job : FlowExecutionSourceKind.Script,
                    AcceptedExecutionId = accepted?.ExecutionId ?? state.AcceptedExecutionId,
                    AcceptedReceipt = accepted is null ? state.AcceptedReceipt : JsonSerializer.SerializeToElement(accepted, Json)
                };
                var output = ExecutionOutput(execution);
                if (!await SaveAsync(Complete(lease, node, resultState, Port(execution.Disposition), output, state.Fields)).ConfigureAwait(false))
                    return Resume("execution-result-not-committed");
                continue;
            }

            var targets = node.Kind == FlowNodeKind.ActionGroup ? node.Actions! :
                new[] { new FlowActionTargetDto(node.Id, node.Kind == FlowNodeKind.InvestigateWithAi
                    ? FlowActionKind.NetclawInvestigation : FlowActionKind.RatelDeskIncident, node.ConnectorId, node.ConnectorRevision) };
            var results = new Dictionary<Guid, FlowIncidentActionResult>();
            var rawFields = FieldsForAction(lease, state);
            async Task<FlowIncidentActionResult> RunTargetAsync(FlowActionTargetDto target)
            {
                var draft = new FlowIncidentActionDraft(lease.Event.TenantId, lease.RunId, target.Id, target.ConnectorId!.Value,
                    target.ConnectorRevision!.Value, lease.SourceInstanceId, FlowActionKeys.Create(lease, target.Id), lease.Event,
                    target.Kind == FlowActionKind.NetclawInvestigation ? state.Fields ?? InitialFields(lease) : rawFields)
                { Kind = target.Kind, AiSettings = target.Kind == FlowActionKind.NetclawInvestigation ? node.AiSettings : null,
                    AiDeadlineAtUtc = target.Kind == FlowActionKind.NetclawInvestigation ? state.DeadlineAtUtc : null };
                if (target.Kind == FlowActionKind.RatelDeskIncident && lease.Event.ExecutionEvent is not null)
                {
                    var attempts = FlowRecoveryEvidence.FromOutputs(lease, current!.Outputs, Order(current));
                    var enriched = await services.GetRequiredService<FlowIncidentEvidenceReader>().ReadAsync(draft,
                        current.Investigation?.ActionNodeId, attempts, ct).ConfigureAwait(false);
                    if (enriched.Disposition == FlowIncidentEvidenceDisposition.Waiting)
                        return new(FlowIncidentActionResultKind.RetryableSafe, "incident-investigation-pending");
                    if (enriched.Disposition != FlowIncidentEvidenceDisposition.Ready || enriched.Draft is null)
                        return new(FlowIncidentActionResultKind.Unavailable, enriched.Code ?? "incident-evidence-unavailable");
                    draft = enriched.Draft;
                }
                return await executeAction(draft, ct).ConfigureAwait(false);
            }
            // Findings must be final before an incident in the same independent group freezes its semantic payload.
            foreach (var target in targets.Where(target => target.Kind == FlowActionKind.NetclawInvestigation))
            {
                var result = await velox.ExecuteNodeAsync(lease.RunId, node, _ => RunTargetAsync(target), ct).ConfigureAwait(false);
                results.Add(target.Id, result);
                if (result.AiResult is not null)
                {
                    if (!await SaveAsync(current! with { Revision = current.Revision + 1, Investigation = result.AiResult }).ConfigureAwait(false))
                        return Resume("investigation-result-not-committed");
                    state = current!;
                }
            }
            if (!results.Values.Any(result => result.Kind == FlowIncidentActionResultKind.Waiting))
            {
                var others = await Task.WhenAll(targets.Where(target => target.Kind != FlowActionKind.NetclawInvestigation)
                    .Select(async target => (target.Id, Result: await velox.ExecuteNodeAsync(lease.RunId, node, _ => RunTargetAsync(target), ct).ConfigureAwait(false))))
                    .ConfigureAwait(false);
                foreach (var item in others) results.Add(item.Id, item.Result);
            }
            var waitingIds = results.Where(pair => pair.Value.Kind is FlowIncidentActionResultKind.Waiting or FlowIncidentActionResultKind.RetryableSafe)
                .Select(pair => pair.Key).ToArray();
            if (waitingIds.Length > 0)
            {
                var approval = results.Values.Any(result => result.AiResult?.Status == FlowAiResultStatus.AwaitingApproval);
                var waiting = current! with { Revision = current.Revision + 1,
                    Progress = approval ? FlowNodeProgress.AwaitingApproval : FlowNodeProgress.Waiting,
                    WaitingActionIds = node.Kind == FlowNodeKind.InvestigateWithAi ? null : waitingIds,
                    Investigation = results.Values.Select(result => result.AiResult).LastOrDefault(result => result is not null) ?? current.Investigation };
                if (!await SaveAsync(waiting).ConfigureAwait(false)) return Resume("action-wait-not-committed");
                if (!await checkpoints.YieldNodeAsync(lease, current!.Revision, ct).ConfigureAwait(false)) return Resume("action-yield-not-committed");
                return new(approval ? FlowRunStatus.AwaitingApproval :
                    node.Kind == FlowNodeKind.InvestigateWithAi ? FlowRunStatus.WaitingForAi : FlowRunStatus.WaitingForExecution,
                    "action-result-pending") { Continuation = new(waiting.NodeId, waiting.Fields, waiting.Investigation) { Node = waiting } };
            }
            if (node.Kind == FlowNodeKind.InvestigateWithAi)
            {
                var result = results[node.Id];
                var port = AiPort(result);
                var output = AiOutput(result);
                if (!await SaveAsync(Complete(lease, node, current!, port, output, current!.Fields)).ConfigureAwait(false))
                    return Resume("investigation-outcome-not-committed");
            }
            else
            {
                var final = node.Kind == FlowNodeKind.ActionGroup ? FlowActionOutcomes.Aggregate(results.Values.ToArray()) :
                    ActionOutcome(results[node.Id]);
                var output = JsonSerializer.SerializeToElement(new { results = results.Select(pair => new { nodeId = pair.Key,
                    kind = pair.Value.Kind.ToString(), pair.Value.Code, pair.Value.Receipt }) }, Json);
                if (!await SaveAsync(Complete(lease, node, current!, null, output, current!.Fields, final.Status, final.Code)).ConfigureAwait(false))
                    return Resume("action-outcome-not-committed");
            }
        }
        return new(FlowRunStatus.Failed, "runtime-step-limit");
    }

    private sealed record PureDecision(string? Port, JsonElement? Output, FlowIncidentFieldsDto? Fields = null, string? Error = null);
    private static bool IsPure(FlowNodeKind kind) => kind is FlowNodeKind.AlertRaised or FlowNodeKind.ExecutionEvent or
        FlowNodeKind.Condition or FlowNodeKind.MapIncident or FlowNodeKind.Finish;
    private static PureDecision Pure(FlowRunLease lease, FlowNodeDto node, FlowNodeCheckpointDto state)
    {
        switch (node.Kind)
        {
            case FlowNodeKind.ExecutionEvent:
                return new(FlowNodeCatalogue.Event, JsonSerializer.SerializeToElement(lease.Event.ExecutionEvent, Json));
            case FlowNodeKind.AlertRaised:
                return new(FlowNodeCatalogue.Event, JsonSerializer.SerializeToElement(lease.Event.Data, Json));
            case FlowNodeKind.Condition:
                if (node.Predicate is { } predicate)
                {
                    var result = FlowValueEvaluation.Matches(predicate, lease.Event.ExecutionEvent, lease.Event.Data, state.Outputs);
                    return result.Available ? new(result.Result ? FlowNodeCatalogue.True : FlowNodeCatalogue.False,
                        JsonSerializer.SerializeToElement(new { result = result.Result }, Json)) :
                        new(null, null, Error: SafeCode(result.Code));
                }
                var matched = lease.Event.Data is { } alert && node.Condition is { } condition && FlowPureEvaluation.Matches(condition, alert);
                return new(matched ? FlowNodeCatalogue.True : FlowNodeCatalogue.False, JsonSerializer.SerializeToElement(new { result = matched }, Json));
            case FlowNodeKind.MapIncident:
                try
                {
                    var fields = FlowTypedMapping.Map(node.Mapping!, lease.Event.ExecutionEvent, lease.Event.Data, state.Inputs, state.Investigation);
                    return new(FlowNodeCatalogue.Result, JsonSerializer.SerializeToElement(fields, Json), fields);
                }
                catch (InvalidOperationException) { return new(null, null, Error: "mapping-input-unavailable"); }
            case FlowNodeKind.Finish:
                return new(null, JsonSerializer.SerializeToElement(new { outcome = "completed" }, Json), state.Fields);
            default: throw new InvalidOperationException("runtime-pure-node");
        }
    }

    private static FlowNodeCheckpointDto Complete(FlowRunLease lease, FlowNodeDto node, FlowNodeCheckpointDto state,
        string? port, JsonElement? output, FlowIncidentFieldsDto? fields, FlowRunStatus? terminal = null, string? code = null)
    {
        var edge = port is null ? null : lease.Version.Graph.Edges.Single(candidate => candidate.SourceNodeId == node.Id && candidate.SourcePort == port);
        var outputs = state.Outputs.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (output is { } value) outputs[node.Id] = value;
        var step = new FlowNodeTraceDto(state.Sequence, node.Id, node.Name, FlowTraceStepStatus.Completed, port ?? code ?? "completed",
            FlowTraceProvenance.Actual, Snapshot(JsonSerializer.SerializeToElement(state.Inputs, Json)), Snapshot(output))
        { SelectedPort = port, EdgeId = edge?.Id, NextNodeId = edge?.TargetNodeId, Attempt = state.Attempt };
        return state with { Revision = state.Revision + 1, Progress = FlowNodeProgress.Completed,
            Output = output, Outputs = outputs, SelectedPort = port, SelectedEdgeId = edge?.Id, NextNodeId = edge?.TargetNodeId,
            Fields = fields, WaitingActionIds = null, Trace = Append(lease, state.Trace, step), ResultStatus = terminal, ResultCode = code };
    }
    private static FlowNodeCheckpointDto Fail(FlowRunLease lease, FlowNodeDto node, FlowNodeCheckpointDto state, string code)
    {
        var step = new FlowNodeTraceDto(state.Sequence, node.Id, node.Name, FlowTraceStepStatus.Unavailable, code,
            FlowTraceProvenance.Actual, Snapshot(JsonSerializer.SerializeToElement(state.Inputs, Json)), new(FlowTraceDataState.Missing))
        { ErrorCode = code, Attempt = state.Attempt };
        return state with { Revision = state.Revision + 1, Progress = FlowNodeProgress.Failed,
            ResultStatus = FlowRunStatus.Failed, ResultCode = code, Trace = Append(lease, state.Trace, step) };
    }
    private static FlowTraceDto Append(FlowRunLease lease, FlowTraceDto? trace, FlowNodeTraceDto step)
    {
        var steps = (trace?.Steps ?? []).Append(step).ToArray();
        if (steps.Sum(value => Bytes(value.Input) + Bytes(value.Output)) > FlowTraceLimits.MaximumSnapshotTotalBytes)
            steps[^1] = step with { Input = Reference(step.Input), Output = Reference(step.Output) };
        return new(2, lease.Version.ConfigurationHash, FlowTraceProvenance.Actual, steps,
            trace?.Truncated == true || steps[^1].Input.State == FlowTraceDataState.Truncated || steps[^1].Output.State == FlowTraceDataState.Truncated);
    }
    private static int Bytes(FlowTraceSnapshotDto snapshot) => snapshot.Value is { } value ? Encoding.UTF8.GetByteCount(value.GetRawText()) : 0;
    private static FlowTraceSnapshotDto Reference(FlowTraceSnapshotDto snapshot) =>
        snapshot.Value is not { } value ? snapshot : new(FlowTraceDataState.Missing) { Reference = "snapshot-budget-unavailable" };
    private static FlowTraceSnapshotDto Snapshot(JsonElement? input)
    {
        if (input is not { } value || value.ValueKind == JsonValueKind.Undefined) return new(FlowTraceDataState.Missing);
        var raw = value.GetRawText(); var bytes = Encoding.UTF8.GetByteCount(raw);
        if (bytes > FlowTraceLimits.MaximumSnapshotBytes)
            return new(FlowTraceDataState.Truncated) { OriginalUtf8Bytes = bytes,
                Excerpt = string.Concat(raw.EnumerateRunes().Take(FlowTraceLimits.MaximumExcerptLength / 2).Select(rune => rune.ToString())) };
        var empty = value.ValueKind switch { JsonValueKind.String => value.GetString() == "",
            JsonValueKind.Array => value.GetArrayLength() == 0, JsonValueKind.Object => !value.EnumerateObject().Any(), _ => false };
        return new(value.ValueKind == JsonValueKind.Null ? FlowTraceDataState.Null : empty ? FlowTraceDataState.Empty : FlowTraceDataState.Present) { Value = value };
    }
    private static FlowExecutionActionResult ConfirmTerminal(FlowRunLease lease, FlowNodeDto node, FlowExecutionActionResult result)
    {
        if (result.Receipt is not { } receipt || result.Disposition is not (FlowExecutionActionDisposition.Succeeded or
            FlowExecutionActionDisposition.Failed or FlowExecutionActionDisposition.TimedOut or FlowExecutionActionDisposition.Cancelled))
            return result;
        if (result.TerminalEvent is not { } terminal)
            return new(FlowExecutionActionDisposition.Pending, "execution-terminal-fact-pending", receipt);
        var expected = result.Disposition switch
        {
            FlowExecutionActionDisposition.Succeeded => FlowExecutionOutcome.Succeeded,
            FlowExecutionActionDisposition.Failed => FlowExecutionOutcome.Failed,
            FlowExecutionActionDisposition.TimedOut => FlowExecutionOutcome.TimedOut,
            _ => FlowExecutionOutcome.Cancelled
        };
        if (!FlowExecutionEventValidator.Validate(terminal).Valid || !FlowExecutionEventProvenance.IsNative(terminal) ||
            terminal.TenantId != lease.Event.TenantId || terminal.ExecutionId != receipt.ExecutionId ||
            terminal.SourceKind != (receipt.Kind == ExecutionAdmissionKind.Job ? FlowExecutionSourceKind.Job : FlowExecutionSourceKind.Script) ||
            terminal.Outcome != expected || terminal.Causation?.FlowRunId != lease.RunId || terminal.Causation.NodeId != node.Id ||
            terminal.Causation.ParentEventId != lease.Event.ExecutionEvent?.EventId)
            return new(FlowExecutionActionDisposition.Unknown, "execution-terminal-fact-conflict", receipt);
        return result;
    }
    private static JsonElement ExecutionOutput(FlowExecutionActionResult result)
    {
        var output = result.Output;
        var values = new Dictionary<string, object?> { ["disposition"] = result.Disposition, ["code"] = result.Code,
            ["receipt"] = result.Receipt, ["terminalEvent"] = result.TerminalEvent, ["outcome"] = Port(result.Disposition),
            ["outputAvailable"] = output?.OutputAvailable ?? false, ["truncated"] = output?.Truncated ?? false };
        if (output is not null)
        {
            values["executionId"] = output.ExecutionId; values["attemptId"] = output.AttemptId;
            values["definition"] = output.Definition; values["target"] = output.Target;
            if (output.ExitCode is { } exit) values["exitCode"] = exit;
            if (output.Summary is { } summary) values["summary"] = summary;
            if (output.StandardOutput is { } stdout) values["stdout"] = stdout;
            if (output.StandardError is { } stderr) values["stderr"] = stderr;
            if (output.Data is { } data) values["data"] = data;
            values["references"] = output.References;
        }
        return JsonSerializer.SerializeToElement(values, Json);
    }
    private static JsonElement AiOutput(FlowIncidentActionResult result)
    {
        var values = new Dictionary<string, object?> { ["status"] = result.AiResult?.Status.ToString() ?? "Unavailable",
            ["code"] = result.Code, ["aiResult"] = result.AiResult };
        if (result.AiResult?.Summary is { } summary) values["summary"] = summary;
        if (result.AiResult?.Evidence is { } evidence) values["evidence"] = evidence;
        if (result.AiResult?.Recommendations is { } recommendations) values["recommendations"] = recommendations;
        return JsonSerializer.SerializeToElement(values, Json);
    }
    private static string Port(FlowExecutionActionDisposition value) => value switch
    {
        FlowExecutionActionDisposition.Succeeded => FlowNodeCatalogue.Succeeded, FlowExecutionActionDisposition.Failed => FlowNodeCatalogue.Failed,
        FlowExecutionActionDisposition.TimedOut => FlowNodeCatalogue.TimedOut, FlowExecutionActionDisposition.Cancelled => FlowNodeCatalogue.Cancelled,
        FlowExecutionActionDisposition.Unsupported => FlowNodeCatalogue.Unsupported, FlowExecutionActionDisposition.Denied => FlowNodeCatalogue.Denied,
        _ => FlowNodeCatalogue.Unknown
    };
    private static string AiPort(FlowIncidentActionResult result) => result.AiResult?.Status switch
    {
        FlowAiResultStatus.Completed => FlowNodeCatalogue.Succeeded, FlowAiResultStatus.Timeout => FlowNodeCatalogue.TimedOut,
        FlowAiResultStatus.Cancelled => FlowNodeCatalogue.Cancelled, FlowAiResultStatus.Unsupported => FlowNodeCatalogue.Unsupported,
        FlowAiResultStatus.DeliveryUnknown => FlowNodeCatalogue.Unknown,
        _ => result.Kind == FlowIncidentActionResultKind.Unavailable ? FlowNodeCatalogue.Unsupported :
            result.Kind == FlowIncidentActionResultKind.DeliveryUnknown ? FlowNodeCatalogue.Unknown : FlowNodeCatalogue.Failed
    };
    private static FlowRuntimeResult ActionOutcome(FlowIncidentActionResult result) => new(result.Kind switch
    {
        FlowIncidentActionResultKind.Succeeded => FlowRunStatus.Succeeded, FlowIncidentActionResultKind.DeliveryUnknown => FlowRunStatus.DeliveryUnknown,
        _ => FlowRunStatus.Failed
    }, result.Code);
    private static IReadOnlyList<Guid> Order(FlowNodeCheckpointDto state) => (state.Trace?.Steps ?? []).OrderBy(step => step.Sequence).Select(step => step.NodeId).ToArray();
    private static int RecoveryOrdinal(FlowRunLease lease, FlowNodeCheckpointDto state) =>
        (lease.Event.ExecutionEvent?.Causation?.RecoveryAttempt ?? 0) + 1 + lease.Version.Graph.Nodes.Count(node =>
            node.Kind == FlowNodeKind.RerunOriginalExecution && state.Outputs.TryGetValue(node.Id, out var value) &&
            value.TryGetProperty("receipt", out var receipt) && receipt.ValueKind == JsonValueKind.Object);
    internal static FlowIncidentFieldsDto FieldsForAction(FlowRunLease lease, FlowNodeCheckpointDto state)
    {
        var lastMap = Order(state).Select(id => lease.Version.Graph.Nodes.Single(node => node.Id == id))
            .LastOrDefault(node => node.Kind == FlowNodeKind.MapIncident);
        if (lastMap?.Mapping is not { } mapping) return state.Fields ?? InitialFields(lease);
        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in lastMap.Inputs ?? new Dictionary<string, FlowInputBindingDto>())
        {
            var resolved = FlowValueEvaluation.Resolve(pair.Value, lease.Event.ExecutionEvent, lease.Event.Data, state.Outputs);
            if (!resolved.Available || resolved.Value is not { } value) throw new InvalidOperationException("mapping-input-unavailable");
            inputs.Add(pair.Key, value);
        }
        return FlowTypedMapping.Map(mapping, lease.Event.ExecutionEvent, lease.Event.Data, inputs, state.Investigation);
    }
    internal static FlowIncidentFieldsDto InitialFields(FlowRunLease lease) => lease.Event.ExecutionEvent is { } source
        ? new(("Execution " + source.Outcome + ": " + (source.Definition.Name ?? source.ExecutionId))[..Math.Min(200,
            ("Execution " + source.Outcome + ": " + (source.Definition.Name ?? source.ExecutionId)).Length)],
            "Original execution outcome: " + source.Outcome + ". Raw status: " + source.RawStatus + ".")
        : new("Monitoring alert", "Original monitoring alert.");
    private static string SafeCode(string code) => code.Length is > 0 and <= 128 &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_') ? code : "node-input-unavailable";
}

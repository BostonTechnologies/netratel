using System.Text.Json;
using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Flows;
using VeloxDev.Core.WorkflowSystem.CompilerEx;
using VeloxDev.MVVM;
using VeloxDev.WorkflowSystem;

namespace NetRatel.Infrastructure.Flows;

/// <summary>The only backend Velox entry. Each run has new allowlisted models, helpers and RuntimeContext.</summary>
public sealed class VeloxFlowRuntimeAdapter : IFlowRuntimeAdapter
{
    public async Task<FlowRuntimeResult> ExecuteAsync(FlowRunLease lease,
        Func<FlowIncidentActionDraft, CancellationToken, Task<FlowIncidentActionResult>> executeAction, CancellationToken cancellationToken = default)
    {
        if (!FlowGraphValidator.ValidatePublished(lease.Version.Graph).Valid || !FlowContractValidation.ValidEnvelope(lease.Event) ||
            lease.Version.TenantId != lease.Event.TenantId || lease.Version.Id != lease.Event.FlowVersionId || lease.SourceInstanceId == Guid.Empty ||
            FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(lease.Version.Graph, FlowPersistenceService.Json)) != lease.Version.ConfigurationHash)
            return new(FlowRunStatus.Failed, "runtime-input-invalid");
        var tree = new TreeDefaultViewModel();
        var state = new RunState(lease, executeAction);
        try
        {
            var models = new Dictionary<Guid, NodeDefaultViewModel>();
            var ports = new Dictionary<(Guid NodeId, bool Output), SlotDefaultViewModel>();
            var startId = lease.Continuation?.ResumeNodeId ?? lease.Version.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.AlertRaised).Id;
            var path = new HashSet<Guid> { startId };
            var currentId = startId;
            while (lease.Version.Graph.Edges.SingleOrDefault(edge => edge.SourceNodeId == currentId) is { } nextEdge && path.Add(nextEdge.TargetNodeId)) currentId = nextEdge.TargetNodeId;
            if (!lease.Version.Graph.Nodes.Any(node => node.Id == startId)) return new(FlowRunStatus.Failed, "runtime-checkpoint-invalid");
            foreach (var definition in lease.Version.Graph.Nodes.Where(node => path.Contains(node.Id)))
            {
                var node = new NodeDefaultViewModel(); node.SetHelper(new AllowlistedNodeHelper(definition, state)); models.Add(definition.Id, node);
                await CompleteCommandAsync(tree.CreateNodeCommand, node, cancellationToken).ConfigureAwait(false);
                if (definition.Kind != FlowNodeKind.AlertRaised) await AddPortAsync(node, definition.Id, false, ports, cancellationToken).ConfigureAwait(false);
                if (!FlowGraphValidator.IsTerminal(definition.Kind)) await AddPortAsync(node, definition.Id, true, ports, cancellationToken).ConfigureAwait(false);
            }
            foreach (var edge in lease.Version.Graph.Edges.Where(edge => path.Contains(edge.SourceNodeId) && path.Contains(edge.TargetNodeId)))
            {
                await CompleteCommandAsync(tree.SendConnectionCommand, ports[(edge.SourceNodeId, true)], cancellationToken).ConfigureAwait(false);
                await CompleteCommandAsync(tree.ReceiveConnectionCommand, ports[(edge.TargetNodeId, false)], cancellationToken).ConfigureAwait(false);
                await CompleteCommandAsync(tree.ResetVirtualLinkCommand, null, cancellationToken).ConfigureAwait(false);
            }
            if (tree.Links.Count != path.Count - 1) return new(FlowRunStatus.Failed, "runtime-graph-adaptation");
            var trigger = lease.Version.Graph.Nodes.Single(node => node.Id == startId);
            // Root compilation is a plan; the actual engine runs only on this explicit backend ingress path.
            var compiler = new CompilerViewModel();
            var compiled = await compiler.CompileAsync(models[trigger.Id], CompileRole.Root, cancellationToken).ConfigureAwait(false);
            if (compiled.Count != 1) return new(FlowRunStatus.Failed, "runtime-plan");
            var context = new RuntimeContext { Uid = lease.RunId };
            await new RuntimeEngine().RunAsync(compiled[0], context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (context.EndedWithError || state.Steps > FlowLimits.MaximumNodes) return new(FlowRunStatus.Failed, "runtime-failed");
            return state.Result ?? (state.Skipped ? new(FlowRunStatus.Skipped, "condition-false") : new(FlowRunStatus.Failed, "action-not-reached"));
        }
        finally { await tree.GetHelper().CloseAsync().ConfigureAwait(false); }
    }

    private static async Task AddPortAsync(NodeDefaultViewModel node, Guid id, bool output, Dictionary<(Guid, bool), SlotDefaultViewModel> ports, CancellationToken ct)
    {
        var slot = new SlotDefaultViewModel(); ports.Add((id, output), slot);
        await CompleteCommandAsync(node.CreateSlotCommand, slot, ct).ConfigureAwait(false);
        await CompleteCommandAsync(slot.SetChannelCommand, output ? SlotChannel.OneTarget : SlotChannel.OneSource, ct).ConfigureAwait(false);
    }
    private static async Task CompleteCommandAsync(IVeloxCommand command, object? parameter, CancellationToken ct)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Matches(CommandEventArgs args) => ReferenceEquals(args.Parameter, parameter) || Equals(args.Parameter, parameter);
        void Done(CommandEventArgs args) { if (Matches(args)) completed.TrySetResult(); }
        void Failed(CommandEventArgs args) { if (Matches(args)) completed.TrySetException(new InvalidOperationException("runtime-command")); }
        void Canceled(CommandEventArgs args) { if (Matches(args)) completed.TrySetCanceled(); }
        command.Completed += Done; command.Failed += Failed; command.Canceled += Canceled;
        try { await command.ExecuteAsync(parameter).ConfigureAwait(false); await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        finally { command.Completed -= Done; command.Failed -= Failed; command.Canceled -= Canceled; }
    }
    private sealed class RunState(FlowRunLease lease, Func<FlowIncidentActionDraft, CancellationToken, Task<FlowIncidentActionResult>> action)
    {
        public FlowRunLease Lease { get; } = lease;
        public Func<FlowIncidentActionDraft, CancellationToken, Task<FlowIncidentActionResult>> Action { get; } = action;
        public int Steps { get; set; }
        public bool Skipped { get; set; }
        public FlowIncidentFieldsDto? Fields { get; set; } = lease.Continuation?.Fields;
        public FlowAiResultDto? AiResult { get; set; } = lease.Continuation?.AiResult;
        public bool Paused { get; set; }
        public FlowRuntimeResult? Result { get; set; }
    }
    private sealed class AllowlistedNodeHelper(FlowNodeDto definition, RunState state) : NodeHelper
    {
        public override async Task<object?> ReceiveAsync(ITaskContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++state.Steps > FlowLimits.MaximumNodes) throw new InvalidOperationException("runtime-step-limit");
            if (state.Skipped || state.Paused) return null;
            switch (definition.Kind)
            {
                case FlowNodeKind.AlertRaised: return state.Lease.Event.Data;
                case FlowNodeKind.Condition:
                    state.Skipped = !FlowPureEvaluation.Matches(definition.Condition!, state.Lease.Event.Data);
                    return state.Skipped ? null : state.Lease.Event.Data;
                case FlowNodeKind.MapIncident:
                    state.Fields = FlowPureEvaluation.Map(definition.Mapping!, state.Lease.Event.Data); return state.Fields;
                case FlowNodeKind.CreateIncident:
                case FlowNodeKind.InvestigateWithAi:
                    var mapping = state.Lease.Version.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident).Mapping!;
                    var incidentFields = FlowPureEvaluation.Map(mapping, state.Lease.Event.Data,
                        definition.Kind == FlowNodeKind.InvestigateWithAi ? null : state.AiResult);
                    var draft = new FlowIncidentActionDraft(state.Lease.Event.TenantId, state.Lease.RunId, definition.Id,
                        definition.ConnectorId!.Value, definition.ConnectorRevision!.Value, state.Lease.SourceInstanceId,
                        FlowActionKeys.Create(state.Lease, definition.Id), state.Lease.Event, incidentFields)
                    { Kind = definition.Kind == FlowNodeKind.InvestigateWithAi ? FlowActionKind.NetclawInvestigation : FlowActionKind.RatelDeskIncident,
                        AiSettings = definition.AiSettings,
                        AiDeadlineAtUtc = definition.Kind == FlowNodeKind.InvestigateWithAi
                            ? (state.Lease.CreatedAtUtc == default ? state.Lease.Event.OccurredAtUtc : state.Lease.CreatedAtUtc).AddMinutes(definition.AiSettings?.DeadlineMinutes ?? 15) : null };
                    var result = await state.Action(draft, ct).ConfigureAwait(false);
                    if (definition.Kind == FlowNodeKind.InvestigateWithAi)
                    {
                        state.AiResult = result.AiResult;
                        if (result.Kind == FlowIncidentActionResultKind.Waiting)
                        {
                            state.Paused = true;
                            state.Result = new(result.AiResult?.Status == FlowAiResultStatus.AwaitingApproval ? FlowRunStatus.AwaitingApproval : FlowRunStatus.WaitingForAi, result.Code)
                            { Continuation = new(definition.Id, state.Fields, result.AiResult) };
                            return null;
                        }
                        if (result.Kind != FlowIncidentActionResultKind.Succeeded &&
                            (definition.AiSettings?.FailureBehavior == FlowAiFailureBehavior.StopFlow || result.AiResult is null)) state.Paused = true;
                    }
                    state.Result = new(result.Kind switch { FlowIncidentActionResultKind.Succeeded => FlowRunStatus.Succeeded,
                        FlowIncidentActionResultKind.RetryableSafe => FlowRunStatus.RetryWaiting, FlowIncidentActionResultKind.DeliveryUnknown => FlowRunStatus.DeliveryUnknown, _ => FlowRunStatus.Failed }, result.Code);
                    return definition.Kind == FlowNodeKind.InvestigateWithAi ? new FlowAiOutputDto(state.Lease.Event.Data, state.AiResult) : result.Receipt;
                case FlowNodeKind.ActionGroup:
                    state.Fields = FlowPureEvaluation.Map(state.Lease.Version.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident).Mapping!, state.Lease.Event.Data, state.AiResult);
                    // Every action owns a separate scope/receipt. Start all bounded siblings so one
                    // slow destination cannot consume their opportunity to run within this lease.
                    var results = await Task.WhenAll(definition.Actions!.Select(action => ExecuteChildAsync(action, ct))).ConfigureAwait(false);
                    state.Result = FlowActionOutcomes.Aggregate(results);
                    if (state.Result.Status is FlowRunStatus.WaitingForAi or FlowRunStatus.AwaitingApproval)
                        state.Result = state.Result with { Continuation = new(definition.Id, state.Fields, state.AiResult) };
                    return null;
                default: throw new InvalidOperationException("runtime-node-kind");
            }
        }

        private async Task<FlowIncidentActionResult> ExecuteChildAsync(FlowActionTargetDto target, CancellationToken ct)
        {
            var draft = new FlowIncidentActionDraft(state.Lease.Event.TenantId, state.Lease.RunId, target.Id,
                target.ConnectorId!.Value, target.ConnectorRevision!.Value, state.Lease.SourceInstanceId,
                FlowActionKeys.Create(state.Lease, target.Id), state.Lease.Event, state.Fields!) { Kind = target.Kind,
                    AiDeadlineAtUtc = target.Kind == FlowActionKind.NetclawInvestigation
                        ? (state.Lease.CreatedAtUtc == default ? state.Lease.Event.OccurredAtUtc : state.Lease.CreatedAtUtc).AddMinutes(15) : null };
            try { return await state.Action(draft, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(FlowIncidentActionResultKind.Failed, "action-cancelled"); }
            catch (Exception) { return new(FlowIncidentActionResultKind.Failed, "action-execution-failed"); }
        }
    }
}

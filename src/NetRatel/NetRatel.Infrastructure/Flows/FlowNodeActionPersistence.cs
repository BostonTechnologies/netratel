using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

public sealed partial class FlowPersistenceService
{
    private async Task<FlowActionExecutionState?> GetOrCreateNodeActionAsync(OrchestratorDbContext db,
        FlowRunLease lease, FlowIncidentActionDraft draft, CancellationToken ct)
    {
        if (draft.RunId != lease.RunId || draft.TenantId != lease.Event.TenantId || draft.SourceInstanceId != lease.SourceInstanceId ||
            draft.IdempotencyKey != FlowActionKeys.Create(lease, draft.ActionNodeId) || Serialize(draft.Event) != Serialize(lease.Event))
            return null;
        var target = FlowGraphValidator.ActionTargets(lease.Version.Graph).SingleOrDefault(action => action.Id == draft.ActionNodeId);
        if (target is null || target.Kind != draft.Kind || target.ConnectorId != draft.ConnectorId ||
            target.ConnectorRevision != draft.ConnectorRevision) return null;
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        var run = await CurrentLeaseAsync(db, lease, ct).ConfigureAwait(false);
        var state = run?.ContinuationJson is null ? null : Parse<FlowContinuationDto>(run.ContinuationJson).Node;
        if (state is null || state.Progress != FlowNodeProgress.Dispatching || !FlowNodeCheckpointRules.Valid(lease, state)) return null;
        var node = lease.Version.Graph.Nodes.SingleOrDefault(node => node.Id == state.NodeId);
        if (node is null || (node.Kind == FlowNodeKind.ActionGroup
            ? node.Actions?.Any(action => action.Id == draft.ActionNodeId) != true : node.Id != draft.ActionNodeId)) return null;
        var configured = draft.Kind == FlowActionKind.NetclawInvestigation ? node.AiSettings : null;
        if (Serialize(configured) != Serialize(draft.AiSettings) ||
            (draft.Kind == FlowActionKind.NetclawInvestigation ? draft.AiDeadlineAtUtc != state.DeadlineAtUtc : draft.AiDeadlineAtUtc is not null))
            return null;
        var action = await db.FlowActions.SingleOrDefaultAsync(row => row.TenantId == draft.TenantId &&
            row.RunId == draft.RunId && row.NodeId == draft.ActionNodeId, ct).ConfigureAwait(false);
        if (action is not null)
        {
            // The previously frozen draft wins even after a worker crash or a later mapping/AI node.
            if (Serialize(Parse<FlowIncidentActionDraft>(action.DraftJson)) != Serialize(draft)) return null;
            if (action.Status == FlowActionStatus.Dispatching && action.LeaseFence < lease.Fence &&
                (draft.Kind is not (FlowActionKind.RatelDeskIncident or FlowActionKind.NetclawInvestigation) ||
                    action.PreparedJson is not null && !Parse<FlowIncidentActionRequest>(action.PreparedJson).SupportsSafeReplay))
            { action.Status = FlowActionStatus.DeliveryUnknown; action.Code = "interrupted-delivery-unknown"; action.LeaseFence = lease.Fence; }
            else if (action.Status is FlowActionStatus.Pending or FlowActionStatus.RetryWaiting or FlowActionStatus.WaitingForAi or FlowActionStatus.AwaitingApproval)
                action.LeaseFence = lease.Fence;
        }
        else
        {
            FlowIncidentActionDraft expected;
            try
            {
                var raw = draft.Kind == FlowActionKind.NetclawInvestigation ? state.Fields ?? FlowGraphV2Runtime.InitialFields(lease) :
                    FlowGraphV2Runtime.FieldsForAction(lease, state);
                expected = draft with { Fields = raw };
                if (draft.Kind == FlowActionKind.RatelDeskIncident && lease.Event.ExecutionEvent is not null)
                {
                    var attempts = FlowRecoveryEvidence.FromOutputs(lease, state.Outputs,
                        (state.Trace?.Steps ?? []).OrderBy(step => step.Sequence).Select(step => step.NodeId).ToArray());
                    var evidence = await new FlowIncidentEvidenceReader(db).ReadAsync(expected, state.Investigation?.ActionNodeId,
                        attempts, ct).ConfigureAwait(false);
                    if (evidence.Disposition != FlowIncidentEvidenceDisposition.Ready || evidence.Draft is null) return null;
                    expected = evidence.Draft;
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return null; }
            if (Serialize(expected) != Serialize(draft)) return null;
            action = new FlowActionRecord { RunId = draft.RunId, NodeId = draft.ActionNodeId, TenantId = draft.TenantId,
                IdempotencyKey = draft.IdempotencyKey, DraftJson = Serialize(draft), ConnectorRevision = draft.ConnectorRevision,
                Status = FlowActionStatus.Pending, LeaseFence = lease.Fence };
            db.FlowActions.Add(action);
        }
        if (!await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false)) return null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (!await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false)) return null;
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return ActionState(action);
    }

    private async Task<bool> CompleteNodeRunAsync(OrchestratorDbContext db, FlowRunLease lease, FlowRuntimeResult result, CancellationToken ct)
    {
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        var run = await CurrentLeaseAsync(db, lease, ct).ConfigureAwait(false);
        if (run is null) return false;
        var state = run.ContinuationJson is null ? null : Parse<FlowContinuationDto>(run.ContinuationJson).Node;
        if (result.Status == FlowRunStatus.RetryWaiting)
        {
            // A timeout/shutdown between canonical acceptance and checkpoint save retries the same action key.
            // The persisted token, not the in-memory prefix, governs restart.
            run.Status = FlowRunStatus.RetryWaiting; run.Code = result.Code;
            run.NextAttemptAtUtc = Now.AddSeconds(5); run.CompletedAtUtc = null;
        }
        else
        {
            if (state?.Progress is not (FlowNodeProgress.Completed or FlowNodeProgress.Failed) ||
                state.NextNodeId is not null || state.ResultStatus != result.Status || state.ResultCode != result.Code)
                return false;
            // Only the selected, completed path determines this run. Inactive branch actions never become pending work.
            run.Status = result.Status; run.Code = result.Code; run.CompletedAtUtc = Now; run.NextAttemptAtUtc = null;
        }
        run.LeaseToken = null; run.LeaseOwner = null; run.LeaseExpiresAtUtc = null;
        if (!await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false)) return false;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (!await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false)) return false;
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

public sealed partial class FlowPersistenceService
{
    private static bool ValidRuntimeGraph(FlowGraphDto graph) => graph.SchemaVersion switch
    {
        FlowLimits.SchemaVersion => FlowGraphValidator.ValidatePublished(graph).Valid,
        FlowGraphV2Limits.SchemaVersion => FlowGraphV2Validator.ValidatePublished(graph).Valid,
        _ => false
    };

    public Task<FlowCheckpointWriteDisposition> SaveNodeCheckpointAsync(FlowRunLease lease,
        int expectedRevision, FlowNodeCheckpointDto checkpoint, CancellationToken ct = default) => WithDb(async (db, services) =>
    {
        if (lease.Version.Graph.SchemaVersion != FlowGraphV2Limits.SchemaVersion || expectedRevision < 0 ||
            checkpoint.Revision != expectedRevision + 1 || !FlowNodeCheckpointRules.Valid(lease, checkpoint))
            return FlowCheckpointWriteDisposition.Invalid;
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (!db.Database.IsNpgsql() || transaction is null) return FlowCheckpointWriteDisposition.Denied;
        var dispatching = checkpoint.Progress == FlowNodeProgress.Dispatching;
        Task<FlowDispatchDecision> AdmitAsync() =>
            services.GetRequiredService<IFlowTransactionAdmission>().CanStartActionAsync(db, lease, ct);
        // Acquire source/configuration locks in the same order as the existing accepted-write path,
        // before taking the Flow run lock. Pure progress never starts an external effect.
        if (dispatching && !(await AdmitAsync().ConfigureAwait(false)).Allowed)
            return FlowCheckpointWriteDisposition.Denied;
        var run = await CurrentLeaseAsync(db, lease, ct).ConfigureAwait(false);
        if (run is null) return FlowCheckpointWriteDisposition.LeaseLost;
        var previous = run.ContinuationJson is null ? null : Parse<FlowContinuationDto>(run.ContinuationJson).Node;
        if ((previous?.Revision ?? 0) != expectedRevision) return FlowCheckpointWriteDisposition.Conflict;
        if (checkpoint.Progress is FlowNodeProgress.Waiting or FlowNodeProgress.AwaitingApproval &&
            checkpoint.WaitingActionIds is { Count: > 0 } waitingIds)
        {
            var waitingRows = await db.FlowActions.AsNoTracking().Where(action => action.TenantId == lease.Event.TenantId &&
                action.RunId == lease.RunId && waitingIds.Contains(action.NodeId) && action.LeaseFence == lease.Fence &&
                (action.Status == FlowActionStatus.RetryWaiting || action.Status == FlowActionStatus.WaitingForAi ||
                    action.Status == FlowActionStatus.AwaitingApproval)).CountAsync(ct).ConfigureAwait(false);
            if (waitingRows != waitingIds.Count) return FlowCheckpointWriteDisposition.Invalid;
        }
        if (!FlowNodeCheckpointRules.CanAdvance(lease, previous, checkpoint)) return FlowCheckpointWriteDisposition.Invalid;
        if (dispatching && !(await AdmitAsync().ConfigureAwait(false)).Allowed ||
            !await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false))
            return FlowCheckpointWriteDisposition.Denied;
        run.ContinuationJson = Serialize(new FlowContinuationDto(checkpoint.NodeId, checkpoint.Fields, checkpoint.Investigation)
            { Node = checkpoint });
        ct.ThrowIfCancellationRequested();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (dispatching && !(await AdmitAsync().ConfigureAwait(false)).Allowed ||
            !await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false))
            return FlowCheckpointWriteDisposition.Denied;
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return FlowCheckpointWriteDisposition.Stored;
    });

    public Task<bool> YieldNodeAsync(FlowRunLease lease, int expectedRevision, CancellationToken ct = default) => WithDb(async (db, _) =>
    {
        if (lease.Version.Graph.SchemaVersion != FlowGraphV2Limits.SchemaVersion) return false;
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (!db.Database.IsNpgsql() || transaction is null) return false;
        var run = await CurrentLeaseAsync(db, lease, ct).ConfigureAwait(false);
        if (run?.ContinuationJson is null) return false;
        var checkpoint = Parse<FlowContinuationDto>(run.ContinuationJson).Node;
        if (checkpoint is null || checkpoint.Revision != expectedRevision ||
            checkpoint.Progress is not (FlowNodeProgress.Waiting or FlowNodeProgress.AwaitingApproval) ||
            !FlowNodeCheckpointRules.Valid(lease, checkpoint) ||
            !await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false))
            return false;
        var kind = lease.Version.Graph.Nodes.Single(node => node.Id == checkpoint.NodeId).Kind;
        run.Status = checkpoint.Progress == FlowNodeProgress.AwaitingApproval ? FlowRunStatus.AwaitingApproval :
            kind == FlowNodeKind.InvestigateWithAi ? FlowRunStatus.WaitingForAi : FlowRunStatus.WaitingForExecution;
        run.NextAttemptAtUtc = Now.AddSeconds(5);
        run.CompletedAtUtc = null; run.LeaseToken = null; run.LeaseOwner = null; run.LeaseExpiresAtUtc = null;
        ct.ThrowIfCancellationRequested();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false)) return false;
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    });
}

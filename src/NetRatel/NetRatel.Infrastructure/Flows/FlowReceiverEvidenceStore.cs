using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

public sealed partial class FlowPersistenceService
{
    Task<DateTimeOffset?> IFlowReceiverEvidenceStore.GetOriginalCreatedAtAsync(FlowRunLease lease,
        CancellationToken ct) => WithDb<DateTimeOffset?>(async (db, _) =>
    {
        await using var tx = await BeginAsync(db, ct);
        var run = await CurrentLeaseAsync(db, lease, ct);
        if (run is null) return null;
        if (tx is not null) await tx.CommitAsync(ct);
        return run.CreatedAtUtc;
    });

    Task<RatelDeskDispatchEvidence?> IFlowReceiverEvidenceStore.ReadAsync(FlowRunLease lease,
        Guid nodeId, CancellationToken ct) => WithDb<RatelDeskDispatchEvidence?>(async (db, services) =>
    {
        await using var tx = await BeginAsync(db, ct);
        var run = await CurrentLeaseAsync(db, lease, ct); if (run is null) return null;
        var action = await db.FlowActions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == lease.Event.TenantId && x.RunId == lease.RunId && x.NodeId == nodeId, ct);
        var record = await db.Set<FlowReceiverEvidenceRecord>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == lease.Event.TenantId && x.RunId == lease.RunId && x.NodeId == nodeId, ct);
        if (action is null || record is null || action.PreparedJson is null) return null;
        var prepared = Parse<RatelDeskReceiverPreparationV2>(record.PreparationJson);
        if (record.TenantId != run.TenantId || record.SchemaVersion != 2 || prepared.SchemaVersion != 2 ||
            record.ReceiverIdempotencyKey != prepared.ReceiverIdempotencyKey ||
            record.ReceiverFingerprint != prepared.ReceiverFingerprint ||
            record.OriginalCreatedAtUtc != run.CreatedAtUtc || record.AutomaticReplayUntilUtc != prepared.AutomaticReplayUntilUtc ||
            prepared.EvidenceFingerprint != record.EvidenceFingerprint ||
            ReceiverPreparationBuilder.EvidenceHash(prepared) != record.EvidenceFingerprint ||
            prepared.Peer.LocalTenantId != lease.Event.TenantId || prepared.Peer.SourceInstanceId != lease.SourceInstanceId ||
            !ReceiverPreparedBinding.Valid(prepared, Parse<FlowIncidentActionRequest>(action.PreparedJson),
                Parse<FlowIncidentActionDraft>(action.DraftJson), run.CreatedAtUtc,
                services.GetRequiredService<IRatelDeskReceiverFingerprint>()))
            throw new InvalidOperationException("persisted-receiver-evidence-invalid");
        if (tx is not null) await tx.CommitAsync(ct);
        return new(prepared, record.MayHaveCommitted, action.Attempts, record.FirstPostAttemptAtUtc,
            record.MayHaveCommitted && action.Attempts == FlowLimits.MaximumActionAttempts && !record.FinalReconciliationAttempted);
    });

    Task<bool> IFlowReceiverEvidenceStore.SavePreparedAsync(FlowRunLease lease, Guid nodeId,
        RatelDeskReceiverPreparationV2 evidence, CancellationToken ct) => WithDb((db, services) =>
        SaveReceiverPreparationAsync(db, services, lease, nodeId, null, evidence, ct));

    Task<bool> IFlowReceiverEvidenceStore.SavePreparedActionAsync(FlowRunLease lease, Guid nodeId,
        FlowIncidentActionRequest request, RatelDeskReceiverPreparationV2 evidence, CancellationToken ct) => WithDb((db, services) =>
        SaveReceiverPreparationAsync(db, services, lease, nodeId, request, evidence, ct));

    private async Task<bool> SaveReceiverPreparationAsync(OrchestratorDbContext db, IServiceProvider services,
        FlowRunLease lease, Guid nodeId, FlowIncidentActionRequest? request, RatelDeskReceiverPreparationV2 evidence, CancellationToken ct)
    {
        await using var tx = await BeginAsync(db, ct).ConfigureAwait(false);
        var run = await CurrentLeaseAsync(db, lease, ct).ConfigureAwait(false); if (run is null) return false;
        var action = await db.FlowActions.SingleOrDefaultAsync(x => x.TenantId == lease.Event.TenantId &&
            x.RunId == lease.RunId && x.NodeId == nodeId, ct).ConfigureAwait(false);
        if (action is null || action.Status is not (FlowActionStatus.Pending or FlowActionStatus.RetryWaiting) ||
            action.Attempts != 0 || action.LeaseFence != lease.Fence || request is null && action.PreparedJson is null) return false;
        var prepared = request ?? Parse<FlowIncidentActionRequest>(action.PreparedJson!);
        var draft = Parse<FlowIncidentActionDraft>(action.DraftJson);
        var serialized = Serialize(evidence);
        if (!FlowContractValidation.ValidPrepared(prepared, draft) || !prepared.SupportsSafeReplay ||
            action.PreparedJson is not null && Serialize(Parse<FlowIncidentActionRequest>(action.PreparedJson)) != Serialize(prepared) ||
            evidence.SchemaVersion != 2 || evidence.ConnectorId != prepared.ConnectorId ||
            evidence.ConnectorRevision != prepared.ConnectorRevision ||
            evidence.Peer.SourceInstanceId != lease.SourceInstanceId || evidence.Peer.LocalTenantId != run.TenantId ||
            evidence.Peer.OrganizationId != prepared.Target.OrganizationId || evidence.Peer.CustomerId != prepared.Target.CustomerId ||
            evidence.OriginalActionCreatedAtUtc != run.CreatedAtUtc || evidence.AutomaticReplayUntilUtc <= run.CreatedAtUtc ||
            evidence.AutomaticReplayUntilUtc > run.CreatedAtUtc + FlowLimits.MaximumRetryAge ||
            !RatelDeskReceiverKey.IsConforming(evidence.ReceiverIdempotencyKey) ||
            ReceiverPreparationBuilder.EvidenceHash(evidence) != evidence.EvidenceFingerprint ||
            Encoding.UTF8.GetByteCount(serialized) > 65_536 ||
            !ReceiverPreparedBinding.Valid(evidence, prepared, draft, run.CreatedAtUtc,
                services.GetRequiredService<IRatelDeskReceiverFingerprint>())) return false;
        var existing = await db.Set<FlowReceiverEvidenceRecord>().SingleOrDefaultAsync(x =>
            x.TenantId == lease.Event.TenantId && x.RunId == lease.RunId && x.NodeId == nodeId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (Serialize(Parse<RatelDeskReceiverPreparationV2>(existing.PreparationJson)) != serialized) return false;
            if (tx is not null) await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        // The original V1 serializer and hashes are unchanged. Initial preparation and its
        // additive receiver sidecar commit atomically; an interrupted save leaves neither.
        if (action.PreparedJson is null)
        {
            action.PreparedJson = Serialize(prepared); action.SemanticFingerprint = prepared.SemanticFingerprint;
            action.ConnectorRevision = prepared.ConnectorRevision;
        }
        db.Set<FlowReceiverEvidenceRecord>().Add(new()
        {
            TenantId = lease.Event.TenantId, RunId = lease.RunId, NodeId = nodeId, SchemaVersion = 2, PreparationJson = serialized,
            EvidenceFingerprint = evidence.EvidenceFingerprint, ReceiverIdempotencyKey = evidence.ReceiverIdempotencyKey,
            ReceiverFingerprint = evidence.ReceiverFingerprint, OriginalCreatedAtUtc = run.CreatedAtUtc,
            AutomaticReplayUntilUtc = evidence.AutomaticReplayUntilUtc
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (tx is not null) await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    Task<bool> IFlowReceiverEvidenceStore.SchedulePreparationRetryAsync(FlowRunLease lease, Guid nodeId,
        TimeSpan? retryAfter, CancellationToken ct) => WithDb(async (db, _) =>
    {
        var delay = retryAfter ?? TimeSpan.FromSeconds(5);
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromHours(1)) return false;
        await using var tx = await BeginAsync(db, ct);
        var run = await CurrentLeaseAsync(db, lease, ct); if (run is null) return false;
        var action = await db.FlowActions.SingleOrDefaultAsync(x => x.TenantId == lease.Event.TenantId &&
            x.RunId == lease.RunId && x.NodeId == nodeId, ct);
        if (action is null || action.LeaseFence != lease.Fence || action.Attempts != 0 ||
            action.Status is not (FlowActionStatus.Pending or FlowActionStatus.RetryWaiting) ||
            await db.Set<FlowReceiverEvidenceRecord>().AnyAsync(x => x.RunId == lease.RunId && x.NodeId == nodeId && x.MayHaveCommitted, ct))
            return false;
        if (Now >= run.CreatedAtUtc + FlowLimits.MaximumRetryAge || run.Attempts >= FlowLimits.MaximumActionAttempts * 2)
        { action.Status = FlowActionStatus.Failed; action.Code = "execution-budget-exhausted"; action.NextAttemptAtUtc = null; }
        else
        {
            action.Status = FlowActionStatus.RetryWaiting; action.Code = "receiver-preparation-retry";
            action.NextAttemptAtUtc = Now + delay;
        }
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return true;
    });

    Task<bool> IFlowReceiverEvidenceStore.MarkPostAttemptAsync(FlowRunLease lease, Guid nodeId,
        CancellationToken ct) => WithDb(async (db, services) =>
    {
        await using var tx = await BeginAsync(db, ct);
        var admission = services.GetRequiredService<IFlowTransactionAdmission>();
        ct.ThrowIfCancellationRequested();
        // Current config -> committed OwnerKey -> registered evidence -> series -> exact outbox
        // locks precede the run/action lock. This transaction contains no HTTP work.
        if (!(await admission.CanStartActionAsync(db, lease, ct).ConfigureAwait(false)).Allowed ||
            !await services.GetRequiredService<IFlowExecutionAuthorityVerifier>()
                .AuthorizeAsync(lease.Event.TenantId, lease.Event.Authority, ct).ConfigureAwait(false)) return false;
        var run = await CurrentLeaseAsync(db, lease, ct); if (run is null) return false;
        var action = await db.FlowActions.SingleOrDefaultAsync(x => x.TenantId == lease.Event.TenantId &&
             x.RunId == lease.RunId && x.NodeId == nodeId, ct);
        var record = await db.Set<FlowReceiverEvidenceRecord>().SingleOrDefaultAsync(x =>
            x.TenantId == lease.Event.TenantId && x.RunId == lease.RunId && x.NodeId == nodeId, ct);
        if (action is null || record is null || action.Status != FlowActionStatus.Dispatching ||
            action.LeaseFence != lease.Fence || record.LastPostLeaseFence == lease.Fence || record.FullReceiptJson is not null ||
            action.Attempts is < 0 or >= FlowLimits.MaximumActionAttempts || Now >= record.AutomaticReplayUntilUtc)
            return false;
        if (action.PreparedJson is null || !ReceiverPreparedBinding.Valid(Parse<RatelDeskReceiverPreparationV2>(record.PreparationJson),
                Parse<FlowIncidentActionRequest>(action.PreparedJson), Parse<FlowIncidentActionDraft>(action.DraftJson),
                run.CreatedAtUtc, services.GetRequiredService<IRatelDeskReceiverFingerprint>())) return false;
        action.Attempts = checked(action.Attempts + 1); record.LastPostLeaseFence = lease.Fence;
        record.MayHaveCommitted = true; record.FirstPostAttemptAtUtc ??= Now; record.RowVersion = checked(record.RowVersion + 1);
        if (lease.ExpiresAtUtc <= Now ||
            !(await admission.CanStartActionAsync(db, lease, ct).ConfigureAwait(false)).Allowed) return false;
        ct.ThrowIfCancellationRequested();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (lease.ExpiresAtUtc <= Now ||
            !(await admission.CanStartActionAsync(db, lease, ct).ConfigureAwait(false)).Allowed) return false;
        ct.ThrowIfCancellationRequested();
        if (tx is not null) await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    });

    Task<bool> IFlowReceiverEvidenceStore.SaveReceiptAsync(FlowRunLease lease, Guid nodeId,
        RatelDeskVerifiedReceipt receipt, CancellationToken ct) => WithDb(async (db, services) =>
    {
        await using var tx = await BeginAsync(db, ct);
        if (!await services.GetRequiredService<IFlowExecutionAuthorityVerifier>()
                .AuthorizeAsync(lease.Event.TenantId, lease.Event.Authority, ct).ConfigureAwait(false)) return false;
        var run = await CurrentLeaseAsync(db, lease, ct); if (run is null) return false;
        var action = await db.FlowActions.SingleOrDefaultAsync(x => x.TenantId == lease.Event.TenantId &&
             x.RunId == lease.RunId && x.NodeId == nodeId, ct);
        var record = await db.Set<FlowReceiverEvidenceRecord>().SingleOrDefaultAsync(x =>
            x.TenantId == lease.Event.TenantId && x.RunId == lease.RunId && x.NodeId == nodeId, ct);
        if (Encoding.UTF8.GetByteCount(receipt.ExactAcceptedBodyJson) > NetRatel.Shared.Contracts.RatelDesk.RatelDeskConnectorLimits.MaximumResponseBytes ||
            action is null || record is null || action.LeaseFence != lease.Fence ||
            action.Status != FlowActionStatus.Dispatching) return false;
        var prepared = Parse<RatelDeskReceiverPreparationV2>(record.PreparationJson);
        if (action.PreparedJson is null || !ReceiverPreparedBinding.Valid(prepared,
                Parse<FlowIncidentActionRequest>(action.PreparedJson), Parse<FlowIncidentActionDraft>(action.DraftJson),
                run.CreatedAtUtc, services.GetRequiredService<IRatelDeskReceiverFingerprint>())) return false;
        var verified = ReceiverWireValidation.Receipt(Encoding.UTF8.GetBytes(receipt.ExactAcceptedBodyJson), receipt.Location, prepared);
        // The raw accepted body is immutable, including its initial top-level incident data.
        var serialized = Serialize(verified);
        if (record.FullReceiptJson is not null && Serialize(Parse<RatelDeskVerifiedReceipt>(record.FullReceiptJson)) != serialized) return false;
        record.FullReceiptJson ??= serialized; record.MayHaveCommitted = false; record.RowVersion = checked(record.RowVersion + 1);
        action.ReceiptJson = Serialize(new FlowActionReceiptDto(verified.IncidentId, verified.TrackingId, null));
        action.Status = FlowActionStatus.Succeeded; action.Code = "incident-receipt-verified";
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return true;
    });
}

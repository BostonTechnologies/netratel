using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

public sealed partial class FlowPersistenceService
{
    public Task<FlowRunSummaryDto?> GetOutcomeAsync(int tenantId, Guid runId, CancellationToken ct = default) => WithDb<FlowRunSummaryDto?>(async (db, _) =>
    {
        var run = await db.FlowRuns.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == runId, ct).ConfigureAwait(false);
        return run is null ? null : Summary(run);
    });
    public Task<FlowRunSummaryDto?> FindOutcomeAsync(int tenantId, Guid eventId, Guid flowVersionId, CancellationToken ct = default) => WithDb<FlowRunSummaryDto?>(async (db, _) =>
    {
        var run = await db.FlowRuns.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.EventId == eventId && row.FlowVersionId == flowVersionId, ct).ConfigureAwait(false);
        return run is null ? null : Summary(run);
    });

    public Task<FlowRunLease?> ClaimAsync(Guid workerId, CancellationToken ct = default) => WithDb<FlowRunLease?>(async (db, services) =>
    {
        if (workerId == Guid.Empty) return null;
        // Apply an explicit deployment producer before creating the Flow singleton. The optional
        // store is solely the existing standalone Flow-test registration seam; the production
        // receiver/continuity registration requires it and EnsureAsync always resolves it.
        if (services.GetService<ServiceLinkIdentityStore>() is { } installation)
            _ = await installation.GetAsync(ct).ConfigureAwait(false);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false); var now = Now;
        FlowRunRecord? run;
        if (db.Database.IsNpgsql())
            run = await db.FlowRuns.FromSqlInterpolated($"SELECT * FROM \"FlowRuns\" WHERE \"Status\" IN (1,2,3) AND (\"NextAttemptAtUtc\" IS NULL OR \"NextAttemptAtUtc\" <= {now}) AND (\"LeaseExpiresAtUtc\" IS NULL OR \"LeaseExpiresAtUtc\" <= {now}) ORDER BY \"CreatedAtUtc\", \"Id\" LIMIT 1 FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(ct).ConfigureAwait(false);
        else run = await db.FlowRuns.Where(row => (row.Status == FlowRunStatus.Queued || row.Status == FlowRunStatus.Running || row.Status == FlowRunStatus.RetryWaiting) &&
            (row.NextAttemptAtUtc == null || row.NextAttemptAtUtc <= now) && (row.LeaseExpiresAtUtc == null || row.LeaseExpiresAtUtc <= now)).OrderBy(row => row.CreatedAtUtc).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (run is null) return null;
        if (run.CreatedAtUtc + FlowLimits.MaximumRetryAge <= now || run.Attempts >= FlowLimits.MaximumActionAttempts * 2)
        {
            var actions = await db.FlowActions.Where(action => action.TenantId == run.TenantId && action.RunId == run.Id).ToListAsync(ct).ConfigureAwait(false);
            var uncertainNodes = await db.Set<FlowReceiverEvidenceRecord>().Where(row => row.TenantId == run.TenantId &&
                row.RunId == run.Id && row.MayHaveCommitted).Select(row => row.NodeId).ToListAsync(ct).ConfigureAwait(false);
            foreach (var action in actions.Where(action => action.Status == FlowActionStatus.Dispatching ||
                uncertainNodes.Contains(action.NodeId) && action.Status != FlowActionStatus.Succeeded))
            { action.Status = FlowActionStatus.DeliveryUnknown; action.Code = "interrupted-delivery-unknown"; action.LeaseFence = checked(run.Fence + 1); }
            if (actions.Any(action => action.Status == FlowActionStatus.DeliveryUnknown))
            { run.Status = FlowRunStatus.DeliveryUnknown; run.Code = "delivery-unknown"; }
            else if (actions.Count == 1 && actions[0].Status == FlowActionStatus.Succeeded && actions[0].ReceiptJson is not null)
            { run.Status = FlowRunStatus.Succeeded; run.Code = "execution-completed"; }
            else { run.Status = FlowRunStatus.Failed; run.Code = "execution-budget-exhausted"; }
            run.CompletedAtUtc = now; run.LeaseToken = null; run.LeaseOwner = null; run.LeaseExpiresAtUtc = null; run.Fence++;
            await db.SaveChangesAsync(ct).ConfigureAwait(false); if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false); return null;
        }
        var sourceId = await EnsureSourceInContextAsync(db, ct).ConfigureAwait(false);
        var version = await db.FlowVersions.AsNoTracking().SingleAsync(row => row.TenantId == run.TenantId && row.Id == run.FlowVersionId, ct).ConfigureAwait(false);
        run.Status = FlowRunStatus.Running; run.Fence = checked(run.Fence + 1); run.LeaseToken = Guid.NewGuid(); run.LeaseOwner = workerId;
        run.LeaseExpiresAtUtc = now + FlowLimits.LeaseDuration; run.Attempts++; run.NextAttemptAtUtc = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false); if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(run.Id, run.LeaseToken.Value, workerId, run.Fence, run.LeaseExpiresAtUtc.Value, sourceId, Version(version), Parse<FlowEventEnvelope>(run.EventJson), run.Attempts);
    });

    // Receipt-only recovery may outlive Monitoring evidence, but never the actual Flow lease.
    // Use the same caller transaction and PostgreSQL wall-clock floor as fresh-action admission.
    private async Task<bool> ReceiverLeaseEffectiveAsync(OrchestratorDbContext db, FlowRunLease lease, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!db.Database.IsNpgsql()) return lease.ExpiresAtUtc > Now;
        if (db.Database.CurrentTransaction is not { } transaction) return false;
        return lease.ExpiresAtUtc > await ClientConnectionEpochStore.EffectiveNowAsync(
            (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction(), clock, ct).ConfigureAwait(false);
    }

    private async Task<FlowRunRecord?> CurrentLeaseAsync(OrchestratorDbContext db, FlowRunLease lease, CancellationToken ct)
    {
        FlowRunRecord? run = db.Database.IsNpgsql()
            ? await db.FlowRuns.FromSqlInterpolated($"SELECT * FROM \"FlowRuns\" WHERE \"Id\" = {lease.RunId} AND \"TenantId\" = {lease.Event.TenantId} FOR UPDATE").SingleOrDefaultAsync(ct).ConfigureAwait(false)
            : await db.FlowRuns.SingleOrDefaultAsync(row => row.Id == lease.RunId && row.TenantId == lease.Event.TenantId, ct).ConfigureAwait(false);
        if (!(run is not null && run.Status == FlowRunStatus.Running && run.LeaseToken == lease.Token && run.LeaseOwner == lease.WorkerId && run.Fence == lease.Fence &&
            run.LeaseExpiresAtUtc == lease.ExpiresAtUtc && run.LeaseExpiresAtUtc > Now && run.FlowVersionId == lease.Version.Id &&
            run.EventFingerprint == FlowContractValidation.Hash(Encoding.UTF8.GetBytes(Serialize(lease.Event))))) return null;
        var version = await db.FlowVersions.AsNoTracking().SingleAsync(row => row.TenantId == run.TenantId && row.Id == run.FlowVersionId, ct).ConfigureAwait(false);
        if (lease.Version.TenantId != run.TenantId || lease.Version.FlowId != run.FlowId || lease.Version.ConfigurationHash != version.ConfigurationHash ||
            !FlowGraphValidator.ValidatePublished(lease.Version.Graph).Valid ||
            FlowContractValidation.Hash(Encoding.UTF8.GetBytes(Serialize(lease.Version.Graph))) != version.ConfigurationHash ||
            !await db.FlowRuntimeIdentity.AnyAsync(row => row.Id == 1 && row.SourceInstanceId == lease.SourceInstanceId, ct).ConfigureAwait(false)) return null;
        return run;
    }
    private static FlowActionExecutionState ActionState(FlowActionRecord row) => new(row.Status, row.Attempts,
        row.PreparedJson is null ? null : Parse<FlowIncidentActionRequest>(row.PreparedJson), row.ReceiptJson is null ? null : Parse<FlowActionReceiptDto>(row.ReceiptJson), row.Code, row.NextAttemptAtUtc);

    public Task<FlowActionExecutionState?> GetOrCreateActionAsync(FlowRunLease lease, FlowIncidentActionDraft draft, CancellationToken ct = default) => WithDb<FlowActionExecutionState?>(async (db, _) =>
    {
        if (draft.RunId != lease.RunId || draft.TenantId != lease.Event.TenantId || draft.SourceInstanceId != lease.SourceInstanceId ||
            draft.IdempotencyKey != FlowActionKeys.Create(lease, draft.ActionNodeId) || Serialize(draft.Event) != Serialize(lease.Event)) return null;
        var node = lease.Version.Graph.Nodes.SingleOrDefault(node => node.Id == draft.ActionNodeId);
        if (node is null || node.Kind != FlowNodeKind.CreateIncident || node.ConnectorId != draft.ConnectorId || node.ConnectorRevision != draft.ConnectorRevision) return null;
        var mapping = lease.Version.Graph.Nodes.SingleOrDefault(node => node.Kind == FlowNodeKind.MapIncident)?.Mapping;
        if (mapping is null) return null;
        try { if (Serialize(FlowPureEvaluation.Map(mapping, lease.Event.Data)) != Serialize(draft.Fields)) return null; }
        catch (InvalidOperationException) { return null; }
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (await CurrentLeaseAsync(db, lease, ct).ConfigureAwait(false) is null) return null;
        var action = await db.FlowActions.SingleOrDefaultAsync(row => row.RunId == lease.RunId && row.NodeId == draft.ActionNodeId && row.TenantId == draft.TenantId, ct).ConfigureAwait(false);
        if (action is not null)
        {
            if (Serialize(Parse<FlowIncidentActionDraft>(action.DraftJson)) != Serialize(draft)) return null;
            if (action.Status == FlowActionStatus.Dispatching && action.LeaseFence < lease.Fence && action.PreparedJson is not null && !Parse<FlowIncidentActionRequest>(action.PreparedJson).SupportsSafeReplay)
            { action.Status = FlowActionStatus.DeliveryUnknown; action.Code = "interrupted-delivery-unknown"; action.LeaseFence = lease.Fence; }
            else if (action.Status is FlowActionStatus.Pending or FlowActionStatus.RetryWaiting) action.LeaseFence = lease.Fence;
            await db.SaveChangesAsync(ct).ConfigureAwait(false); if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
            return ActionState(action);
        }
        action = new() { RunId = lease.RunId, NodeId = draft.ActionNodeId, TenantId = draft.TenantId, IdempotencyKey = draft.IdempotencyKey,
            DraftJson = Serialize(draft), ConnectorRevision = draft.ConnectorRevision, Status = FlowActionStatus.Pending, LeaseFence = lease.Fence };
        db.FlowActions.Add(action); await db.SaveChangesAsync(ct).ConfigureAwait(false); if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return ActionState(action);
    });

    public Task<bool> SavePreparedActionAsync(FlowRunLease lease, Guid nodeId, FlowIncidentActionRequest request, CancellationToken ct = default) => WithDb(async (db, _) =>
    {
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (await CurrentLeaseAsync(db, lease, ct).ConfigureAwait(false) is null) return false;
        var row = await db.FlowActions.SingleOrDefaultAsync(row => row.TenantId == lease.Event.TenantId && row.RunId == lease.RunId && row.NodeId == nodeId, ct).ConfigureAwait(false);
        if (row is null || row.Status is not (FlowActionStatus.Pending or FlowActionStatus.RetryWaiting) || row.Attempts != 0 ||
            !FlowContractValidation.ValidPrepared(request, Parse<FlowIncidentActionDraft>(row.DraftJson))) return false;
        if (row.PreparedJson is not null) return Serialize(Parse<FlowIncidentActionRequest>(row.PreparedJson)) == Serialize(request);
        row.PreparedJson = Serialize(request); row.SemanticFingerprint = request.SemanticFingerprint; row.ConnectorRevision = request.ConnectorRevision;
        await db.SaveChangesAsync(ct).ConfigureAwait(false); if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false); return true;
    });

    public Task<FlowActionExecutionState?> StartActionAsync(FlowRunLease lease, Guid nodeId, CancellationToken ct = default) => WithDb<FlowActionExecutionState?>(async (db, services) =>
    {
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        Task<FlowDispatchDecision> CheckFreshAdmissionAsync() =>
            services.GetRequiredService<IFlowTransactionAdmission>().CanStartActionAsync(db, lease, ct);
        ct.ThrowIfCancellationRequested();
        var receiverHint = await db.Set<FlowReceiverEvidenceRecord>().AsNoTracking().SingleOrDefaultAsync(evidence =>
            evidence.TenantId == lease.Event.TenantId && evidence.RunId == lease.RunId && evidence.NodeId == nodeId, ct).ConfigureAwait(false);
        var requireFreshMonitoringAdmission = receiverHint is null;
        if (receiverHint is not null)
        {
            var actionHint = await db.FlowActions.AsNoTracking().SingleOrDefaultAsync(action => action.TenantId == lease.Event.TenantId &&
                action.RunId == lease.RunId && action.NodeId == nodeId, ct).ConfigureAwait(false);
            // No fresh effect starts under a retired owner or stale Monitoring evidence. A prior
            // possible commit may still enter a lease-fenced authenticated read-only lookup.
            requireFreshMonitoringAdmission = !receiverHint.MayHaveCommitted && actionHint?.Status != FlowActionStatus.Dispatching;
            if (requireFreshMonitoringAdmission &&
                !(await CheckFreshAdmissionAsync().ConfigureAwait(false)).Allowed)
                return null;
            if (!await services.GetRequiredService<IFlowExecutionAuthorityVerifier>()
                    .AuthorizeAsync(lease.Event.TenantId, lease.Event.Authority, ct).ConfigureAwait(false)) return null;
        }
        if (receiverHint is null && !(await CheckFreshAdmissionAsync().ConfigureAwait(false)).Allowed) return null;
        if (!await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false)) return null;
        if (await CurrentLeaseAsync(db, lease, ct).ConfigureAwait(false) is null) return null;
        var row = await db.FlowActions.SingleOrDefaultAsync(row => row.TenantId == lease.Event.TenantId && row.RunId == lease.RunId && row.NodeId == nodeId, ct).ConfigureAwait(false);
        if (row is null || row.PreparedJson is null) return null;
        if (row.Status is FlowActionStatus.Succeeded or FlowActionStatus.Failed or FlowActionStatus.DeliveryUnknown) return ActionState(row);
        var receiver = await db.Set<FlowReceiverEvidenceRecord>().SingleOrDefaultAsync(evidence =>
            evidence.TenantId == lease.Event.TenantId && evidence.RunId == lease.RunId && evidence.NodeId == nodeId, ct).ConfigureAwait(false);
        if (receiver is not null)
        {
            if (row.Status == FlowActionStatus.Dispatching && row.LeaseFence == lease.Fence) return null;
            if (row.NextAttemptAtUtc > Now) return ActionState(row);
            if (row.Attempts >= FlowLimits.MaximumActionAttempts)
            {
                if (!receiver.MayHaveCommitted || receiver.FinalReconciliationAttempted)
                {
                    row.Status = receiver.MayHaveCommitted ? FlowActionStatus.DeliveryUnknown : FlowActionStatus.Failed;
                    row.Code = receiver.MayHaveCommitted ? "final-reconciliation-exhausted-unknown" : "action-budget-exhausted";
                    row.LeaseFence = lease.Fence;
                    if (!await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false)) return null;
                    ct.ThrowIfCancellationRequested();
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    if (!await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false)) return null;
                    ct.ThrowIfCancellationRequested();
                    if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return ActionState(row);
                }
                receiver.FinalReconciliationAttempted = true; receiver.RowVersion = checked(receiver.RowVersion + 1);
            }
            // A recovered in-flight call is reconciled before any replay, including the fifth POST.
            // Read-only recovery does not consume another POST attempt or extend either horizon.
            row.Status = FlowActionStatus.Dispatching; row.LeaseFence = lease.Fence; row.NextAttemptAtUtc = null;
            if (!await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false) || requireFreshMonitoringAdmission &&
                !(await CheckFreshAdmissionAsync().ConfigureAwait(false)).Allowed) return null;
            ct.ThrowIfCancellationRequested();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            // Existing locks prevent replacement but do not freeze the owner's deadline.
            if (!await ReceiverLeaseEffectiveAsync(db, lease, ct).ConfigureAwait(false) || requireFreshMonitoringAdmission &&
                !(await CheckFreshAdmissionAsync().ConfigureAwait(false)).Allowed) return null;
            ct.ThrowIfCancellationRequested();
            if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
            return ActionState(row);
        }
        if (row.Status == FlowActionStatus.Dispatching)
        {
            if (row.LeaseFence == lease.Fence) return null;
            if (!Parse<FlowIncidentActionRequest>(row.PreparedJson).SupportsSafeReplay)
            { row.Status = FlowActionStatus.DeliveryUnknown; row.Code = "interrupted-delivery-unknown"; row.LeaseFence = lease.Fence; }
        }
        if (row.Status != FlowActionStatus.DeliveryUnknown)
        {
            if (row.NextAttemptAtUtc > Now) return ActionState(row);
            if (row.Attempts >= FlowLimits.MaximumActionAttempts)
            {
                // A safe receiver permits an exact replay; it does not establish whether the last
                // interrupted send committed when the replay budget has already been consumed.
                var uncertain = row.Status == FlowActionStatus.Dispatching;
                row.Status = uncertain ? FlowActionStatus.DeliveryUnknown : FlowActionStatus.Failed;
                row.Code = uncertain ? "interrupted-attempt-budget-unknown" : "action-budget-exhausted"; row.LeaseFence = lease.Fence;
            }
            else { row.Status = FlowActionStatus.Dispatching; row.LeaseFence = lease.Fence; row.Attempts++; row.NextAttemptAtUtc = null; }
        }
        ct.ThrowIfCancellationRequested();
        if (!(await CheckFreshAdmissionAsync().ConfigureAwait(false)).Allowed || lease.ExpiresAtUtc <= Now) return null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!(await CheckFreshAdmissionAsync().ConfigureAwait(false)).Allowed || lease.ExpiresAtUtc <= Now) return null;
        ct.ThrowIfCancellationRequested();
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return ActionState(row);
    });

    public Task<bool> CompleteActionAsync(FlowRunLease lease, Guid nodeId, FlowIncidentActionResult result, CancellationToken ct = default) => WithDb(async (db, _) =>
    {
        if (!ValidResult(result)) return false;
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (await CurrentLeaseAsync(db, lease, ct).ConfigureAwait(false) is null) return false;
        var row = await db.FlowActions.SingleOrDefaultAsync(row => row.TenantId == lease.Event.TenantId && row.RunId == lease.RunId && row.NodeId == nodeId, ct).ConfigureAwait(false);
        if (row is null || row.Status is not (FlowActionStatus.Dispatching or FlowActionStatus.Pending or FlowActionStatus.RetryWaiting)) return false;
        var receiver = await db.Set<FlowReceiverEvidenceRecord>().SingleOrDefaultAsync(evidence =>
            evidence.TenantId == lease.Event.TenantId && evidence.RunId == lease.RunId && evidence.NodeId == nodeId, ct).ConfigureAwait(false);
        if (receiver is not null)
        {
            // Full original receiver receipts commit through SaveReceiptAsync only.
            if (result.Kind == FlowIncidentActionResultKind.Succeeded) return false;
            if (receiver.MayHaveCommitted && result.Kind is FlowIncidentActionResultKind.Failed or FlowIncidentActionResultKind.Unavailable)
                result = new(FlowIncidentActionResultKind.DeliveryUnknown, result.Code);
        }
        if (row.LeaseFence != lease.Fence && !(row.LeaseFence < lease.Fence && row.Status == FlowActionStatus.Dispatching && result.Kind == FlowIncidentActionResultKind.DeliveryUnknown)) return false;
        if (row.Status is FlowActionStatus.Pending or FlowActionStatus.RetryWaiting &&
            result.Kind is not (FlowIncidentActionResultKind.Failed or FlowIncidentActionResultKind.Unavailable) &&
            !(receiver?.MayHaveCommitted == true && result.Kind == FlowIncidentActionResultKind.DeliveryUnknown)) return false;
        row.LeaseFence = lease.Fence;
        row.Code = result.Code;
        row.Status = result.Kind switch { FlowIncidentActionResultKind.Succeeded => FlowActionStatus.Succeeded,
            FlowIncidentActionResultKind.DeliveryUnknown => FlowActionStatus.DeliveryUnknown, FlowIncidentActionResultKind.RetryableSafe => FlowActionStatus.RetryWaiting, _ => FlowActionStatus.Failed };
        if (row.Status == FlowActionStatus.RetryWaiting)
        {
            if (row.Attempts >= FlowLimits.MaximumActionAttempts &&
                !(row.Attempts == FlowLimits.MaximumActionAttempts && receiver?.MayHaveCommitted == true && !receiver.FinalReconciliationAttempted))
            {
                row.Status = receiver?.MayHaveCommitted == true ? FlowActionStatus.DeliveryUnknown : FlowActionStatus.Failed;
                row.Code = receiver?.MayHaveCommitted == true ? "action-budget-exhausted-unknown" : "action-budget-exhausted";
                row.NextAttemptAtUtc = null;
            }
            else row.NextAttemptAtUtc = Now + (result.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, row.Attempts - 1))));
        }
        if (result.Receipt is not null) row.ReceiptJson = Serialize(result.Receipt);
        await db.SaveChangesAsync(ct).ConfigureAwait(false); if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false); return true;
    });

    public Task<bool> CompleteRunAsync(FlowRunLease lease, FlowRuntimeResult result, CancellationToken ct = default) => WithDb(async (db, _) =>
    {
        if (!Enum.IsDefined(result.Status) || result.Status is FlowRunStatus.Queued or FlowRunStatus.Running || !ValidCode(result.Code)) return false;
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false); var run = await CurrentLeaseAsync(db, lease, ct).ConfigureAwait(false); if (run is null) return false;
        var actions = await db.FlowActions.Where(row => row.TenantId == run.TenantId && row.RunId == run.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var action in actions.Where(row => row.Status == FlowActionStatus.Dispatching))
        { action.Status = FlowActionStatus.DeliveryUnknown; action.Code = "action-receipt-not-committed"; action.LeaseFence = lease.Fence; }
        if (actions.Any(row => row.Status == FlowActionStatus.DeliveryUnknown)) result = new(FlowRunStatus.DeliveryUnknown, "delivery-unknown");
        else if (actions.Any(row => row.Status == FlowActionStatus.RetryWaiting)) result = new(FlowRunStatus.RetryWaiting, "retry-waiting");
        else if (actions.Any(row => row.Status == FlowActionStatus.Failed)) result = new(FlowRunStatus.Failed, actions.First(row => row.Status == FlowActionStatus.Failed).Code ?? "action-failed");
        else if (actions.Count == 1 && actions[0].Status == FlowActionStatus.Succeeded && actions[0].ReceiptJson is not null)
            result = new(FlowRunStatus.Succeeded, "execution-completed");
        else if (result.Status == FlowRunStatus.Succeeded && (actions.Count != 1 || actions[0].Status != FlowActionStatus.Succeeded)) return false;
        run.Status = result.Status; run.Code = result.Code; run.NextAttemptAtUtc = result.Status == FlowRunStatus.RetryWaiting ? actions.Where(row => row.NextAttemptAtUtc.HasValue).Min(row => row.NextAttemptAtUtc) : null;
        run.CompletedAtUtc = result.Status == FlowRunStatus.RetryWaiting ? null : Now; run.LeaseToken = null; run.LeaseOwner = null; run.LeaseExpiresAtUtc = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false); if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false); return true;
    });

    public Task<int> PruneHistoryAsync(CancellationToken ct = default) => WithDb(async (db, _) =>
    {
        var cutoff = Now - FlowLimits.HistoryRetention;
        var ids = await db.FlowRuns.Where(row => row.CompletedAtUtc < cutoff && row.Status != FlowRunStatus.DeliveryUnknown).OrderBy(row => row.CompletedAtUtc).Select(row => row.Id).Take(100).ToListAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0) return 0;
        // Ambiguous deliveries retain their keys/semantic evidence for explicit reconciliation.
        if (db.Database.IsRelational()) return await db.FlowRuns.Where(row => ids.Contains(row.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.FlowRuns.RemoveRange(await db.FlowRuns.Where(row => ids.Contains(row.Id)).ToListAsync(ct).ConfigureAwait(false)); await db.SaveChangesAsync(ct).ConfigureAwait(false); return ids.Count;
    });
    private static bool ValidCode(string code) => FlowGraphValidator.IsBoundedText(code, 128) && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_');
    private static bool ValidResult(FlowIncidentActionResult result) => Enum.IsDefined(result.Kind) && ValidCode(result.Code) &&
        (result.RetryAfter is null || result.RetryAfter >= TimeSpan.Zero && result.RetryAfter <= TimeSpan.FromHours(1)) &&
        (result.Kind != FlowIncidentActionResultKind.Succeeded || result.Receipt is not null) &&
        (result.Receipt is null || result.Kind == FlowIncidentActionResultKind.Succeeded && FlowGraphValidator.IsBoundedText(result.Receipt.IncidentId, 256) &&
            (result.Receipt.TrackingId is null || FlowGraphValidator.IsBoundedText(result.Receipt.TrackingId, 256)) &&
            (result.Receipt.SafeLink is null || result.Receipt.SafeLink.Length <= 2048 && Uri.TryCreate(result.Receipt.SafeLink, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)));
}

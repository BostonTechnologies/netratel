using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Shared.SystemPairing;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

public sealed partial class FlowRunProcessor
{
    private async Task<FlowIncidentActionResult> ExecuteReceiverActionAsync(FlowRunLease lease, FlowIncidentActionDraft draft,
        FlowActionExecutionState existing, IServiceProvider provider, IFlowReceiverDispatcher receiver, CancellationToken ct)
    {
        var evidenceStore = provider.GetRequiredService<IFlowReceiverEvidenceStore>();
        var clock = provider.GetRequiredService<TimeProvider>();
        Task<FlowDispatchDecision> Admit(CancellationToken token) => AdmitDispatchAsync(provider, lease, token);
        var evidence = await evidenceStore.ReadAsync(lease, draft.ActionNodeId, ct).ConfigureAwait(false);
        var reconcile = evidence?.MayHaveCommitted == true || existing.Status == FlowActionStatus.Dispatching;
        if (!await provider.GetRequiredService<IFlowExecutionAuthorityVerifier>()
                .AuthorizeAsync(lease.Event.TenantId, lease.Event.Authority, ct).ConfigureAwait(false))
            return await DenyReceiverAsync(lease, draft.ActionNodeId, existing, evidence, "execution-authority-revoked", ct).ConfigureAwait(false);
        var gate = await Admit(ct).ConfigureAwait(false);
        // Current suppression/disablement prevents a fresh effect, while authorized read-only
        // reconciliation can still recover an original committed receipt.
        if (!gate.Allowed && !reconcile)
            return await DenyReceiverAsync(lease, draft.ActionNodeId, existing, evidence, gate.Code, ct).ConfigureAwait(false);
        if (evidence is null)
        {
            // Historical V1 requests have no receiver wire-key provenance. Retain their bytes and
            // hashes; a current capability cannot retrofit safe replay onto an earlier dispatch.
            if (existing.Request is not null || existing.Status == FlowActionStatus.Dispatching || existing.Attempts > 0)
            {
                var unavailable = new FlowIncidentActionResult(existing.Status == FlowActionStatus.Dispatching || existing.Attempts > 0
                    ? FlowIncidentActionResultKind.DeliveryUnknown : FlowIncidentActionResultKind.Unavailable,
                    "historical-receiver-evidence-unavailable");
                await store.CompleteActionAsync(lease, draft.ActionNodeId, unavailable, ct).ConfigureAwait(false);
                return unavailable;
            }
            var createdAt = await evidenceStore.GetOriginalCreatedAtAsync(lease, ct).ConfigureAwait(false);
            if (createdAt is null) return new(FlowIncidentActionResultKind.Failed, "action-lease-lost");
            var preparation = await receiver.PrepareReceiverAsync(draft, createdAt.Value, ct).ConfigureAwait(false);
            if (preparation.Status != FlowIncidentPreparationStatus.Ready || preparation.Action is null || preparation.Evidence is null)
            {
                if (preparation.Status == FlowIncidentPreparationStatus.Unavailable && preparation.RetryAfter is not null &&
                    await evidenceStore.SchedulePreparationRetryAsync(lease, draft.ActionNodeId, preparation.RetryAfter, ct).ConfigureAwait(false))
                    return new(FlowIncidentActionResultKind.RetryableSafe, "receiver-preparation-retry", RetryAfter: preparation.RetryAfter);
                var unavailable = new FlowIncidentActionResult(preparation.Status == FlowIncidentPreparationStatus.Unavailable
                    ? FlowIncidentActionResultKind.Unavailable : FlowIncidentActionResultKind.Failed,
                    SafeCode(preparation.Code ?? "receiver-preparation-unavailable"));
                await store.CompleteActionAsync(lease, draft.ActionNodeId, unavailable, ct).ConfigureAwait(false);
                return unavailable;
            }
            if (!FlowContractValidation.ValidPrepared(preparation.Action, draft) ||
                !await evidenceStore.SavePreparedActionAsync(lease, draft.ActionNodeId, preparation.Action, preparation.Evidence, ct).ConfigureAwait(false))
                return new(FlowIncidentActionResultKind.Failed, "receiver-preparation-not-committed");
            evidence = await evidenceStore.ReadAsync(lease, draft.ActionNodeId, ct).ConfigureAwait(false);
            if (evidence is null) return new(FlowIncidentActionResultKind.Failed, "receiver-evidence-not-persisted");
        }
        gate = await Admit(ct).ConfigureAwait(false);
        if (!gate.Allowed && !reconcile)
            return await DenyReceiverAsync(lease, draft.ActionNodeId, existing, evidence, gate.Code, ct).ConfigureAwait(false);
        if (!await provider.GetRequiredService<IFlowExecutionAuthorityVerifier>()
                .AuthorizeAsync(lease.Event.TenantId, lease.Event.Authority, ct).ConfigureAwait(false))
            return await DenyReceiverAsync(lease, draft.ActionNodeId, existing, evidence, "execution-authority-revoked", ct).ConfigureAwait(false);
        var started = await store.StartActionAsync(lease, draft.ActionNodeId, ct).ConfigureAwait(false);
        if (started is null) return new(FlowIncidentActionResultKind.Failed, "action-lease-lost");
        if (Terminal(started) is { } terminal) return terminal;
        if (started.Status == FlowActionStatus.RetryWaiting) return new(FlowIncidentActionResultKind.RetryableSafe, "retry-waiting");
        if (started.Status != FlowActionStatus.Dispatching) return new(FlowIncidentActionResultKind.Failed, "action-state-invalid");
        evidence = evidence with { Attempts = started.Attempts,
            MayHaveCommitted = evidence.MayHaveCommitted || existing.Status == FlowActionStatus.Dispatching };
        RatelDeskReceiverObservation observation;
        try
        {
            observation = await receiver.DispatchReceiverAsync(lease, draft.ActionNodeId, evidence, Admit, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        { observation = new(RatelDeskReceiverObservationKind.AuthenticationRejected, "receiver-current-authority-denied"); }
        catch (PairingException error)
        {
            observation = new(error.StatusCode is 401 or 403
                ? RatelDeskReceiverObservationKind.AuthenticationRejected : RatelDeskReceiverObservationKind.TransientReadFailure,
                error.StatusCode is 401 or 403 ? "receiver-current-authority-denied" : "receiver-current-profile-unavailable");
        }
        catch (Exception)
        { observation = new(RatelDeskReceiverObservationKind.TransientReadFailure, "receiver-operation-interrupted"); }
        // Dispatch may have committed MayHaveCommitted after the original snapshot. Use the
        // durable value even after cancellation, a lost response, or a malformed accepted body.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var current = await evidenceStore.ReadAsync(lease, draft.ActionNodeId, completion.Token).ConfigureAwait(false);
        if (current is null) return new(FlowIncidentActionResultKind.DeliveryUnknown, "action-lease-lost");
        if (observation.Kind == RatelDeskReceiverObservationKind.Committed && observation.Receipt is { } receipt)
        {
            if (!await evidenceStore.SaveReceiptAsync(lease, draft.ActionNodeId, receipt, completion.Token).ConfigureAwait(false))
                return new(FlowIncidentActionResultKind.DeliveryUnknown, "action-receipt-not-committed");
            return new(FlowIncidentActionResultKind.Succeeded, "incident-receipt-verified", new(receipt.IncidentId, receipt.TrackingId, null));
        }
        var result = ReceiverDispatchPolicy.Complete(current, observation, clock.GetUtcNow());
        if (!await store.CompleteActionAsync(lease, draft.ActionNodeId, result, completion.Token).ConfigureAwait(false))
            return new(FlowIncidentActionResultKind.DeliveryUnknown, "action-outcome-not-committed");
        return result;
    }

    private async Task<FlowIncidentActionResult> DenyReceiverAsync(FlowRunLease lease, Guid nodeId,
        FlowActionExecutionState existing, RatelDeskDispatchEvidence? evidence, string code, CancellationToken ct)
    {
        var denied = new FlowIncidentActionResult(evidence?.MayHaveCommitted == true || existing.Status == FlowActionStatus.Dispatching
            ? FlowIncidentActionResultKind.DeliveryUnknown : FlowIncidentActionResultKind.Failed, SafeCode(code));
        await store.CompleteActionAsync(lease, nodeId, denied, ct).ConfigureAwait(false);
        return denied;
    }
}

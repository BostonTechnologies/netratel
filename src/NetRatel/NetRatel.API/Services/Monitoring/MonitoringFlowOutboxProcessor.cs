using NetRatel.Application.Flows;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.API.Services.Monitoring;

/// <summary>Dispatches a new immutable intent once; known and uncertain receipts use flow-store reads only.</summary>
public sealed class MonitoringFlowOutboxProcessor(IMonitoringStore store, IFlowEventIngress ingress,
    IFlowDefinitionService flows, TimeProvider clock)
{
    public async Task<bool> ProcessLeaseAsync(MonitoringOutboxLease lease, CancellationToken cancellationToken)
    {
        if (lease.FlowRunId is { } knownRun)
            return await CompleteKnownRunAsync(lease, knownRun, cancellationToken).ConfigureAwait(false);
        var input = MonitoringFlowEventFactory.Create(lease.Intent);
        if (input is null)
            return await CompleteRejectedAsync(lease, "monitoring_immutable_event_invalid", cancellationToken).ConfigureAwait(false);

        FlowIngressResult admission;
        try { admission = await ingress.EnqueueAsync(input, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // The ingress transaction might already exist. Unknown is later
            // reconciled by exact event/version lookup, never by a new enqueue.
            return await store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.DeliveryUnknown,
                new(null, MonitoringFlowOutcomeKind.DeliveryUnknown, clock.GetUtcNow(), "flow_handoff_unknown"), "flow_handoff_unknown"), cancellationToken).ConfigureAwait(false);
        }
        if (admission.Disposition == FlowIngressDisposition.Conflict)
            return await store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.DeliveryUnknown,
                new(null, MonitoringFlowOutcomeKind.DeliveryUnknown, clock.GetUtcNow(), "flow_event_conflict"), "flow_event_conflict"), cancellationToken).ConfigureAwait(false);
        if (admission.Disposition == FlowIngressDisposition.CapacityExceeded && admission.RunId is null)
            return await store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.Pending, Code: "flow_capacity_pending"), cancellationToken).ConfigureAwait(false);
        if (admission.Disposition == FlowIngressDisposition.Invalid && admission.RunId is null)
            return await CompleteRejectedAsync(lease, "flow_ingress_rejected", cancellationToken).ConfigureAwait(false);
        if (admission.Disposition is not (FlowIngressDisposition.Enqueued or FlowIngressDisposition.Duplicate or FlowIngressDisposition.Disabled) ||
            admission.RunId is not { } realRun || realRun == Guid.Empty)
            return await store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.DeliveryUnknown,
                new(null, MonitoringFlowOutcomeKind.DeliveryUnknown, clock.GetUtcNow(), "flow_handoff_unverified"), "flow_handoff_unverified"), cancellationToken).ConfigureAwait(false);

        // Persist the real run ID under this exact lease before releasing it.
        // False ownership stops here; it cannot cause another ingress call.
        if (!await store.MarkOutboxHandedOffAsync(lease, realRun, cancellationToken).ConfigureAwait(false)) return false;
        return await CompleteKnownRunAsync(lease with { FlowRunId = realRun }, realRun, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ReconcileReceiptAsync(MonitoringUnsettledFlowReceipt receipt, CancellationToken cancellationToken)
    {
        var run = receipt.FlowRunId is { } known
            ? await ingress.GetOutcomeAsync(receipt.Intent.Series.TenantId, known, cancellationToken).ConfigureAwait(false)
            : await ingress.FindOutcomeAsync(receipt.Intent.Series.TenantId, receipt.Intent.EventId,
                receipt.Intent.PublishedFlowVersionId, cancellationToken).ConfigureAwait(false);
        if (run is null || !MonitoringFlowEventFactory.Matches(run, receipt.Intent, receipt.FlowRunId)) return false;
        var outcome = await ReadVerifiedTerminalAsync(receipt.Intent, run, cancellationToken).ConfigureAwait(false);
        return outcome is not null && await store.ReconcileOutboxOutcomeAsync(receipt, outcome, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CompleteKnownRunAsync(MonitoringOutboxLease lease, Guid runId, CancellationToken cancellationToken)
    {
        var run = await ingress.GetOutcomeAsync(lease.Intent.Series.TenantId, runId, cancellationToken).ConfigureAwait(false);
        if (run is null || !MonitoringFlowEventFactory.Matches(run, lease.Intent, runId))
            return await store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.Pending, Code: "flow_receipt_pending"), cancellationToken).ConfigureAwait(false);
        var outcome = await ReadVerifiedTerminalAsync(lease.Intent, run, cancellationToken).ConfigureAwait(false);
        if (outcome is null)
            return await store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.Pending, Code: "flow_run_pending"), cancellationToken).ConfigureAwait(false);
        var status = outcome.Outcome switch
        {
            MonitoringFlowOutcomeKind.Succeeded => MonitoringOutboxStatus.Completed,
            MonitoringFlowOutcomeKind.Skipped => MonitoringOutboxStatus.Skipped,
            MonitoringFlowOutcomeKind.Failed => MonitoringOutboxStatus.Failed,
            _ => MonitoringOutboxStatus.DeliveryUnknown
        };
        return await store.CompleteOutboxAsync(new(lease, status, outcome, outcome.Code), cancellationToken).ConfigureAwait(false);
    }

    private async Task<MonitoringFlowOutcomeDto?> ReadVerifiedTerminalAsync(MonitoringOutboxIntent intent, FlowRunSummaryDto run,
        CancellationToken cancellationToken)
    {
        if (run.Status is FlowRunStatus.Queued or FlowRunStatus.Running or FlowRunStatus.RetryWaiting ||
            !Enum.IsDefined(run.Status) || run.CompletedAtUtc is not { } completed || completed == default || completed > clock.GetUtcNow()) return null;
        var canonical = MonitoringFlowEventFactory.Create(intent);
        if (canonical is null) return null;
        var version = await flows.GetVersionAsync(intent.Series.TenantId, intent.PublishedFlowVersionId, cancellationToken).ConfigureAwait(false);
        if (version is null || version.TenantId != intent.Series.TenantId || version.Id != intent.PublishedFlowVersionId ||
            version.FlowId != run.FlowId || !PublishedMonitoringFlowProvider.HasValidImmutableGraph(version)) return null;
        var detail = await flows.GetRunByIdAsync(intent.Series.TenantId, run.Id, cancellationToken).ConfigureAwait(false);
        if (detail is null || detail.Run != run || detail.Input != canonical.Data || detail.Actions.Count > 1) return null;
        var successful = detail.Actions.Where(action => action.Status == FlowActionStatus.Succeeded && action.Receipt is not null).ToArray();
        // Never discard a persisted success receipt by translating contradictory
        // run metadata into a local failure or an unknown delivery.
        if (run.Status != FlowRunStatus.Succeeded && successful.Length != 0) return null;
        MonitoringIncidentReceiptDto? receipt = null;
        if (run.Status == FlowRunStatus.Succeeded)
        {
            if (successful.Length != 1 || successful[0].Receipt is not { } persisted) return null;
            var link = persisted.SafeLink is { } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                parsed.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(parsed.UserInfo) ? url : null;
            receipt = new(persisted.IncidentId, persisted.TrackingId, link);
            if (!MonitoringContractValidator.TryValidateIncidentReceipt(receipt, out _)) return null;
        }
        var kind = run.Status switch
        {
            FlowRunStatus.Succeeded => MonitoringFlowOutcomeKind.Succeeded,
            FlowRunStatus.Skipped => MonitoringFlowOutcomeKind.Skipped,
            FlowRunStatus.Failed => MonitoringFlowOutcomeKind.Failed,
            _ => MonitoringFlowOutcomeKind.DeliveryUnknown
        };
        var code = run.Code is { Length: > 0 and <= 64 } value &&
            value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
            ? value : kind switch
            {
                MonitoringFlowOutcomeKind.Succeeded => "flow_succeeded", MonitoringFlowOutcomeKind.Skipped => "flow_skipped",
                MonitoringFlowOutcomeKind.Failed => "flow_failed", _ => "flow_delivery_unknown"
            };
        // This is the receipt observation time. CompletedAt remains on the
        // immutable FlowRun; an old completed run can resolve a newer Unknown
        // marker without moving the monitoring outcome clock backwards.
        return new(run.Id, kind, clock.GetUtcNow(), code, receipt);
    }

    private Task<bool> CompleteRejectedAsync(MonitoringOutboxLease lease, string code, CancellationToken cancellationToken)
    {
        // A later no-run response cannot prove what an earlier interrupted
        // transaction did. Keep that history lookup-only and explicit.
        var uncertain = lease.Attempt > 1;
        return store.CompleteOutboxAsync(new(lease, uncertain ? MonitoringOutboxStatus.DeliveryUnknown : MonitoringOutboxStatus.Failed,
            new(null, uncertain ? MonitoringFlowOutcomeKind.DeliveryUnknown : MonitoringFlowOutcomeKind.Failed, clock.GetUtcNow(), code), code), cancellationToken);
    }
}

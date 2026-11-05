using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

public sealed partial class FlowPersistenceService : IFlowExecutionEventIngress
{
    /// <summary>Only immutable server fact and delivery IDs cross this admission boundary.</summary>
    public Task<FlowIngressResult> EnqueueExecutionAsync(FlowExecutionEventAdmissionRequest request,
        CancellationToken ct = default) => WithDb(async (db, services) =>
    {
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (!db.Database.IsNpgsql() || transaction is null)
            return new FlowIngressResult(FlowIngressDisposition.Invalid, Code: "execution-source-transaction-required");
        await TenantLockAsync(db, request.TenantId, ct).ConfigureAwait(false);
        var admission = services.GetRequiredService<FlowExecutionEventAdmission>();
        var contributorReferencesTruncated = false;
        var selection = await admission.LoadAsync(db, request, ct, skipHistoricalContributors: true).ConfigureAwait(false);
        if (selection.Proof is { Trigger.ExecutionTrigger: { AggregateCorrelatedEvents: true, Sources.Count: > 1 } } initial)
        {
            // The ingress, not the claimed delivery's supplied subset, owns first-admission aggregation.
            // The tenant fence makes this committed intent group stable through the acceptance commit.
            var row = await db.OutboxMessages.AsNoTracking().SingleAsync(item => item.Id == initial.DeliveryId, ct).ConfigureAwait(false);
            var delivery = ExecutionLifecycleSubscriptions.ReadDelivery(row.PayloadJson)!;
            var contributors = await ExecutionLifecycleSubscriptionWorker.SelectContributorsAsync(db, delivery,
                await admission.EffectiveNowAsync(db, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            contributorReferencesTruncated = contributors.Truncated;
            selection = await admission.LoadAsync(db, request with { ContributingEventIds = contributors.EventIds }, ct, skipHistoricalContributors: true).ConfigureAwait(false);
        }
        if (selection.Pending) return new(FlowIngressDisposition.CapacityExceeded, Code: "execution-parent-result-pending");
        if (selection.Proof is not { } proof) return new(FlowIngressDisposition.Invalid, Code: selection.Decision.Code);
        var existing = await db.FlowRuns.SingleOrDefaultAsync(row => row.TenantId == request.TenantId &&
            row.FlowVersionId == request.FlowVersionId && row.EventId == proof.Aggregate.AdmissionKey, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            await RecordContributorsAsync(db, existing, proof, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(FlowIngressDisposition.Duplicate, existing.Id, existing.Code);
        }
        var active = new[] { FlowRunStatus.Queued, FlowRunStatus.Running, FlowRunStatus.RetryWaiting,
            FlowRunStatus.WaitingForAi, FlowRunStatus.AwaitingApproval, FlowRunStatus.WaitingForExecution };
        if (await db.FlowRuns.CountAsync(row => row.TenantId == request.TenantId, ct).ConfigureAwait(false) >= FlowLimits.MaximumRunsPerTenant ||
            await db.FlowRuns.CountAsync(row => row.TenantId == request.TenantId && active.Contains(row.Status), ct).ConfigureAwait(false) >= FlowLimits.MaximumQueuedRunsPerTenant)
            return new(FlowIngressDisposition.CapacityExceeded);
        var original = proof.Aggregate.Representative;
        var input = new FlowEventEnvelope(request.TenantId, original.EventId, original.OccurrenceId,
            proof.Version.Id, original.OccurredAtUtc, null, proof.Authority)
        { ExecutionEvent = original, SourceAdmissionKey = proof.Aggregate.AdmissionKey, SourceDeliveryId = proof.DeliveryId, SourceEventIds = proof.Aggregate.ContributingEventIds,
            SourceEventIdsTruncated = contributorReferencesTruncated || proof.Aggregate.Truncated };
        if (!FlowContractValidation.ValidEnvelope(input)) return new(FlowIngressDisposition.Invalid, Code: "execution-source-envelope-invalid");
        var now = await ActivationNowAsync(db, ct).ConfigureAwait(false);
        var admitted = selection.Decision.Allowed;
        var json = Serialize(input);
        var run = new FlowRunRecord { Id = Guid.NewGuid(), TenantId = request.TenantId, FlowId = proof.Version.FlowId,
            FlowVersionId = proof.Version.Id, EventId = proof.Aggregate.AdmissionKey, OccurrenceId = input.OccurrenceId,
            EventJson = json, EventFingerprint = FlowContractValidation.Hash(Encoding.UTF8.GetBytes(json)),
            Status = admitted ? FlowRunStatus.Queued : FlowRunStatus.Failed, CreatedAtUtc = now,
            CompletedAtUtc = admitted ? null : now, Code = admitted ? null : selection.Decision.Code };
        db.FlowRuns.Add(run);
        await RecordContributorsAsync(db, run, proof, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(admitted ? FlowIngressDisposition.Enqueued : FlowIngressDisposition.Disabled, run.Id, run.Code);
    });

    // Each subscription run owns an independent receipt for every contributing immutable source fact.
    // Late duplicates extend provenance receipts; the accepted event/fingerprint never changes.
    private async Task RecordContributorsAsync(OrchestratorDbContext db, FlowRunRecord run, FlowSourceProof proof, CancellationToken ct)
    {
        var consumer = "flow-source:" + run.Id.ToString("N", CultureInfo.InvariantCulture);
        foreach (var fact in proof.Facts)
            if (!await db.OutboxProcessedEvents.AnyAsync(row => row.EventId == fact.EventId && row.ConsumerName == consumer, ct).ConfigureAwait(false))
                db.OutboxProcessedEvents.Add(new OutboxProcessedEvent { EventId = fact.EventId, ConsumerName = consumer, ProcessedUtc = Now });
    }
}

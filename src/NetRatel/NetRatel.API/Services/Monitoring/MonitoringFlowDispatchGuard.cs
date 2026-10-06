using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services.Monitoring;

/// <summary>A scoped exact-intent and current monitoring gate; the flow runtime separately verifies flow/connector execution grants.</summary>
public sealed class MonitoringFlowDispatchGuard(OrchestratorDbContext db,
    IFlowExecutionAuthorityVerifier authority, TimeProvider clock) : IFlowDispatchGuard
{
    public async Task<FlowDispatchDecision> CanDispatchAsync(FlowEventEnvelope input, CancellationToken cancellationToken = default)
    {
        if (!FlowContractValidation.ValidEnvelope(input)) return new(false, "monitoring-event-invalid");
        try
        {
            if (!await authority.AuthorizeAsync(input.TenantId, input.Authority, cancellationToken).ConfigureAwait(false))
                return new(false, "monitoring-authority-unavailable");
            var runId = await db.FlowRuns.AsNoTracking().Where(row => row.TenantId == input.TenantId &&
                row.EventId == input.EventId && row.FlowVersionId == input.FlowVersionId && row.OccurrenceId == input.OccurrenceId)
                .Select(row => (Guid?)row.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (runId is null || !db.Database.IsNpgsql()) return new(false, "monitoring-run-unavailable");
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            // Readiness uses the same durable primitive as StartAction. The latter repeats it in its own accepted-write transaction.
            return await MonitoringStore.LockAndAdmitFlowAsync(db, input, runId.Value, clock, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(false, "monitoring-dispatch-unavailable"); }
    }

}

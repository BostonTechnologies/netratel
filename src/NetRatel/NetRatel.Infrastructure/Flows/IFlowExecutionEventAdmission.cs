using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Flows;

/// <summary>Rechecks a saved source investigation under the caller's transaction after its Flow worker has yielded.</summary>
public interface IFlowExecutionEventAdmission
{
    Task<FlowDispatchDecision> LockAndAdmitInvestigationAsync(OrchestratorDbContext db, FlowEventEnvelope input,
        Guid runId, CancellationToken cancellationToken = default);
}

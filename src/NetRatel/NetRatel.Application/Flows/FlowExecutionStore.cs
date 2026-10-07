using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

public sealed record FlowActionExecutionState(FlowActionStatus Status, int Attempts, FlowIncidentActionRequest? Request,
    FlowActionReceiptDto? Receipt, string? Code, DateTimeOffset? NextAttemptAtUtc = null);
/// <summary>Every mutation verifies the current run lease token/fence/expiry using a scoped transaction.</summary>
public interface IFlowExecutionStore
{
    Task<FlowRunLease?> ClaimAsync(Guid workerId, CancellationToken cancellationToken = default);
    Task<FlowActionExecutionState?> GetOrCreateActionAsync(FlowRunLease lease, FlowIncidentActionDraft draft, CancellationToken cancellationToken = default);
    Task<bool> SavePreparedActionAsync(FlowRunLease lease, Guid nodeId, FlowIncidentActionRequest request, CancellationToken cancellationToken = default);
    Task<FlowActionExecutionState?> StartActionAsync(FlowRunLease lease, Guid nodeId, CancellationToken cancellationToken = default);
    Task<bool> CompleteActionAsync(FlowRunLease lease, Guid nodeId, FlowIncidentActionResult result, CancellationToken cancellationToken = default);
    Task<bool> CompleteRunAsync(FlowRunLease lease, FlowRuntimeResult result, CancellationToken cancellationToken = default);
    Task<int> PruneHistoryAsync(CancellationToken cancellationToken = default);
}

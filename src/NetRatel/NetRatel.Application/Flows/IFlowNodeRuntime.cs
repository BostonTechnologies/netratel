using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>
/// Runs exactly the already-selected allowlisted node in an isolated actual Velox context. The durable
/// token/selected edge is owned by the checkpoint store; this boundary does not infer parallel joins.
/// </summary>
public interface IFlowNodeRuntime
{
    Task<T> ExecuteNodeAsync<T>(Guid runId, FlowNodeDto node,
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
}

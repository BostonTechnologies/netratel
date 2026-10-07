namespace NetRatel.Application.ClientAuth;

// Protected independently of agent.dat: the released fixed-field writer may
// replace that file during rollback without discarding an ambiguous exchange.
public interface IAgentRefreshExchangeStore
{
    Task<PendingAgentRefreshExchange?> LoadPendingExchangeAsync(CancellationToken ct);
    Task<PendingAgentRefreshExchange> BeginRefreshExchangeAsync(PendingAgentRefreshExchange exchange, CancellationToken ct);
    Task CompleteRefreshExchangeAsync(PendingAgentRefreshExchange exchange, string successorRefreshToken, CancellationToken ct);
}

public sealed record PendingAgentRefreshExchange(
    int Version, Guid ExchangeId, string AgentId, string ParentRefreshToken,
    string DeviceKeyHash, string[] RequestedScopes);

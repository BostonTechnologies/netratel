namespace NetRatel.Shared.Operations;

/// <summary>
/// Resolves the persisted ownership of a closed-schema MCP object reference.
/// Implementations must fail closed when an object is missing, deleted, or no
/// longer belongs to the tenant and agent pair in the delegation.
/// </summary>
public interface IMcpOperationObjectTargetResolver
{
    Task<bool> MatchesDelegationAsync(
        McpOperatorDelegation delegation,
        CancellationToken cancellationToken);
}

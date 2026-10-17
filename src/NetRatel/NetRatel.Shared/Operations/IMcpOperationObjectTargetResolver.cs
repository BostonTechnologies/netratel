namespace NetRatel.Shared.Operations;

/// <summary>
/// Resolves the persisted ownership of a closed-schema MCP object reference.
/// Implementations must fail closed when an object is missing, deleted, or no
/// longer belongs to the tenant and agent pair in the delegation.
/// </summary>
public interface IMcpOperationObjectTargetResolver
{
    /// <summary>Resolves only a persisted policy; supplied tenant/agent IDs cannot establish ownership.</summary>
    Task<McpOperationResolvedPolicyTarget?> ResolvePolicyTargetAsync(string reference, CancellationToken cancellationToken)
        => Task.FromResult<McpOperationResolvedPolicyTarget?>(null);

    Task<bool> MatchesDelegationAsync(
        McpOperatorDelegation delegation,
        CancellationToken cancellationToken);
}

public sealed record McpOperationResolvedPolicyTarget(int? TenantId, Guid? AgentId);

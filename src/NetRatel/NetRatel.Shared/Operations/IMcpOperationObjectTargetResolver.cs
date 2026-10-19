namespace NetRatel.Shared.Operations;

/// <summary>
/// Resolves the persisted ownership of a closed-schema MCP object reference.
/// Implementations fail closed when ownership cannot be proved. A deleted
/// object is admitted only for a signed, exact, successful confirmation replay
/// owned by the currently authenticated caller; it remains hidden to reads and
/// fresh previews. Business endpoints still enforce current policy and receipt.
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

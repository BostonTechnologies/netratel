using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>Uses the authoritative evaluator against one current policy snapshot for a search batch.</summary>
public sealed class McpOperatorSearchAuthorization(OrchestratorDbContext db) : IMcpOperatorSearchAuthorization
{
    public async Task<IReadOnlyList<McpOperatorDecision>> EvaluateAsync(
        IReadOnlyList<McpOperatorAccessRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count > 100) throw new ArgumentOutOfRangeException(nameof(requests));
        if (requests.Count == 0) return [];
        var tenants = requests.Select(request => request.TenantId).Distinct().ToArray();
        var environments = requests.Select(request => request.Environment).Distinct().ToArray();
        var policies = await db.McpOperatorPolicies.AsNoTracking()
            .Where(policy => environments.Contains(policy.Environment) &&
                (tenants.Contains(policy.TenantId) ||
                 (policy.Environment == McpOperatorEnvironment.Development && policy.TenantId == 0 &&
                  policy.TargetSelectorKind == McpOperatorTargetSelectorKind.DevelopmentEnvironment)) &&
                policy.LifecycleState == McpOperatorPolicyLifecycleState.Active && policy.DisabledAtUtc == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return requests.Select(request => McpOperatorAuthorization.EvaluateSnapshot(request, policies, now)).ToArray();
    }
}

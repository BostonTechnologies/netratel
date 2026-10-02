using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Infrastructure.Persistence;

public sealed class MonitoringAgentEligibility(IServiceScopeFactory scopes) : IMonitoringAgentEligibility
{
    public async Task<bool> IsEligibleAsync(NetRatel.Application.Presence.ClientKey client, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!client.IsValid) return false;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.Agents.AsNoTracking().AnyAsync(agent => agent.TenantId == client.TenantId && agent.Id == client.AgentId &&
            agent.IsEnabled && agent.Status == AgentStatus.Active && agent.RevokedAtUtc == null && agent.DeletedAtUtc == null &&
            agent.SupersededAtUtc == null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<Guid>> GetEligibleAgentsAsync(int tenantId, CancellationToken cancellationToken)
    {
        if (tenantId <= 0) throw new ArgumentException("invalid_tenant");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var ids = await db.Agents.AsNoTracking().Where(agent => agent.TenantId == tenantId && agent.IsEnabled &&
            agent.Status == AgentStatus.Active && agent.RevokedAtUtc == null && agent.DeletedAtUtc == null && agent.SupersededAtUtc == null)
            .OrderBy(agent => agent.Id).Select(agent => agent.Id).Take(MonitoringLimits.MaximumTargetClients + 1)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Length > MonitoringLimits.MaximumTargetClients) throw new InvalidOperationException("target_capacity_exceeded");
        return ids.ToImmutableArray();
    }
}

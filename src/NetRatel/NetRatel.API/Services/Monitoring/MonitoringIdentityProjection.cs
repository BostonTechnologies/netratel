using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.API.Services.Monitoring;

/// <summary>One bounded directory query per page or selection, scoped before projection.</summary>
public static class MonitoringIdentityProjection
{
    public static async Task<ImmutableArray<MonitoringClientIdentityDto>> ReadAsync(OrchestratorDbContext db, int tenantId,
        IEnumerable<Guid> agentIds, CancellationToken cancellationToken)
    {
        var ids = agentIds.Distinct().ToArray();
        if (tenantId <= 0 || ids.Length > MonitoringLimits.MaximumTargetClients || ids.Contains(Guid.Empty))
            throw new ArgumentException("invalid_monitoring_identity_scope");
        if (ids.Length == 0) return [];
        var rows = await db.Agents.IgnoreQueryFilters().AsNoTracking().Where(agent => agent.TenantId == tenantId && ids.Contains(agent.Id))
            .Select(agent => new { agent.Id, agent.Name, agent.DeviceInfoJson, agent.DeletedAtUtc }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var found = rows.ToDictionary(row => row.Id, row => MonitoringIdentityPresentation.Create(row.Id, row.Name, row.DeviceInfoJson, row.DeletedAtUtc is not null));
        return ids.Select(id => found.GetValueOrDefault(id) ?? new(id, "Client details unavailable", MetadataMissing: true)).ToImmutableArray();
    }
}

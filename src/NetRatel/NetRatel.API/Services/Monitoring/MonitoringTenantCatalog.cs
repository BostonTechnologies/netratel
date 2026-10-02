using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.API.Services.Monitoring;

public interface IMonitoringTenantCatalog
{
    Task<ImmutableArray<MonitoringTenantDto>> GetAsync(int[]? authorizedTenantIds, int maximumCount, CancellationToken cancellationToken);
}

public sealed class MonitoringTenantCatalog(OrchestratorDbContext db) : IMonitoringTenantCatalog
{
    public async Task<ImmutableArray<MonitoringTenantDto>> GetAsync(int[]? authorizedTenantIds, int maximumCount, CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        var query = db.Tenants.AsNoTracking();
        if (authorizedTenantIds is not null) query = query.Where(tenant => authorizedTenantIds.Contains(tenant.Id));
        var result = await query.OrderBy(tenant => tenant.Id).Take(maximumCount + 1)
            .Select(tenant => new MonitoringTenantDto(tenant.Id, tenant.Name)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (result.Count > maximumCount) throw new MonitoringApiException(StatusCodes.Status413PayloadTooLarge, "monitoring_tenant_capacity_exceeded");
        return result.ToImmutableArray();
    }
}

public sealed class MonitoringApiException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

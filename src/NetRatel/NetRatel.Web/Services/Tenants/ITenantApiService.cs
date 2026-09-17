using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Requests;

namespace NetRatel.Web.Services.Tenants;

public interface ITenantApiService
{
    Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default);
    Task CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default);
    Task UpdateTenantAsync(int tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default);
    Task DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default);
}

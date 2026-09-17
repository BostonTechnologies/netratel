using NetRatel.Application.Tenants;

namespace NetRatel.API.Services;

public interface ITenantLookupService
{
    Task<bool> TenantExistsAsync(int tenantId, CancellationToken ct = default);
}

public sealed class PostgresTenantLookupService : ITenantLookupService
{
    private readonly ITenantService _tenants;

    public PostgresTenantLookupService(ITenantService tenants)
    {
        _tenants = tenants;
    }

    public Task<bool> TenantExistsAsync(int tenantId, CancellationToken ct = default)
        => _tenants.ExistsAsync(tenantId, ct);
}

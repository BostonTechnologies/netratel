using System.Net.Http.Json;
using System.Threading;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Requests;

namespace NetRatel.Web.Services.Tenants;

public class TenantApiService : ITenantApiService
{
    private readonly IHttpClientFactory _clientFactory;

    public TenantApiService(IHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    private HttpClient CreateClient() => _clientFactory.CreateClient("OrchestratorApi");

    public async Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var result = await client.GetFromJsonAsync<List<TenantDto>>("/api/v1/tenants", cancellationToken);
        return result ?? new List<TenantDto>();
    }

    public async Task CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/tenants/", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateTenantAsync(int tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var response = await client.PutAsJsonAsync($"/api/v1/tenants/{tenantId}", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var response = await client.DeleteAsync($"/api/v1/tenants/{tenantId}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.Contracts.SoftwareInventory;

namespace NetRatel.Web.Services.SoftwareInventory;

public interface ISoftwareInventoryApiService
{
    Task<SoftwarePage<SoftwareInventoryTenant>> TenantsAsync(int page, CancellationToken ct) => throw new NotSupportedException();
    Task<SoftwareInventoryStatus> StatusAsync(int tenant, Guid agent, CancellationToken ct);
    Task<SoftwareInventoryStatus> RefreshAsync(int tenant, Guid agent, bool force, CancellationToken ct);
    Task<SoftwarePage<SoftwareInstallationDto>> InstallationsAsync(int tenant, SoftwareInventoryQuery query, CancellationToken ct);
    Task<SoftwarePage<SoftwarePrevalenceDto>> AggregateAsync(int tenant, SoftwareInventoryQuery query, CancellationToken ct) => throw new NotSupportedException();
    Task<SoftwarePage<SoftwareChangeDto>> ChangesAsync(int tenant, Guid? agent, int page, CancellationToken ct) => throw new NotSupportedException();
    Task<SoftwareInventoryPolicy> PolicyAsync(int tenant, CancellationToken ct) => throw new NotSupportedException();
    Task SetPolicyAsync(int tenant, SoftwareInventoryPolicy policy, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class SoftwareInventoryApiService(IHttpClientFactory factory) : ISoftwareInventoryApiService
{
    private readonly HttpClient _http = factory.CreateClient("OrchestratorApi");
    public Task<SoftwarePage<SoftwareInventoryTenant>> TenantsAsync(int page, CancellationToken ct) => GetAsync<SoftwarePage<SoftwareInventoryTenant>>($"/api/v2/software-inventory/tenants?page={page}&pageSize=100", ct);
    public Task<SoftwareInventoryStatus> StatusAsync(int tenant, Guid agent, CancellationToken ct) => GetAsync<SoftwareInventoryStatus>($"/api/v2/agents/{tenant}/{agent:D}/software-inventory", ct);
    public async Task<SoftwareInventoryStatus> RefreshAsync(int tenant, Guid agent, bool force, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v2/agents/{tenant}/{agent:D}/software-inventory/refresh", new SoftwareRefreshRequest(force), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SoftwareInventoryStatus>(cancellationToken: ct) ?? throw new HttpRequestException("Empty scan response.");
    }
    public Task<SoftwarePage<SoftwareInstallationDto>> InstallationsAsync(int tenant, SoftwareInventoryQuery query, CancellationToken ct) =>
        GetAsync<SoftwarePage<SoftwareInstallationDto>>(QueryUrl(tenant, "installations", query), ct);
    public Task<SoftwarePage<SoftwarePrevalenceDto>> AggregateAsync(int tenant, SoftwareInventoryQuery query, CancellationToken ct) =>
        GetAsync<SoftwarePage<SoftwarePrevalenceDto>>(QueryUrl(tenant, "aggregate", query), ct);
    public Task<SoftwarePage<SoftwareChangeDto>> ChangesAsync(int tenant, Guid? agent, int page, CancellationToken ct) =>
        GetAsync<SoftwarePage<SoftwareChangeDto>>($"/api/v2/tenants/{tenant}/software-inventory/changes?page={page}&pageSize=50" + (agent.HasValue ? $"&agentId={agent:D}" : ""), ct);
    public Task<SoftwareInventoryPolicy> PolicyAsync(int tenant, CancellationToken ct) => GetAsync<SoftwareInventoryPolicy>($"/api/v2/tenants/{tenant}/software-inventory/policy", ct);
    public async Task SetPolicyAsync(int tenant, SoftwareInventoryPolicy policy, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync($"/api/v2/tenants/{tenant}/software-inventory/policy", policy, ct);
        response.EnsureSuccessStatusCode();
    }
    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await _http.GetFromJsonAsync<T>(path, timeout.Token) ?? throw new HttpRequestException("Empty software inventory response.");
    }
    public static string QueryUrl(int tenant, string resource, SoftwareInventoryQuery query) =>
        $"/api/v2/tenants/{tenant}/software-inventory/{resource}?" + string.Join('&', JsonSerializer.SerializeToElement(query, SoftwareInventoryWire.JsonOptions)
            .EnumerateObject().Where(property => property.Value.ValueKind != JsonValueKind.Null)
            .Select(property => Uri.EscapeDataString(property.Name) + "=" + Uri.EscapeDataString(property.Value.ToString())));
}

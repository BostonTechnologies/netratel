using System.Net.Http.Json;
using System.Net;
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
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new HttpRequestException("The tenant changed. Reopen its current settings before saving again.", null, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new HttpRequestException("You do not have permission to change this tenant's settings.", null, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new HttpRequestException("The tenant settings were rejected. Check the target version and its release channel.", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
    }

    public Task DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default) => DeleteTenantCoreAsync(tenantId, null, cancellationToken);
    public Task DeleteTenantAsync(int tenantId, long expectedVersion, CancellationToken cancellationToken = default) => DeleteTenantCoreAsync(tenantId, expectedVersion, cancellationToken);
    private async Task DeleteTenantCoreAsync(int tenantId, long? expectedVersion, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/tenants/{tenantId}");
        if (expectedVersion is { } version) request.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{version}\""));
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return;
        var detail = response.StatusCode == HttpStatusCode.Conflict
            ? "Resolve the tenant's active dependencies before deleting it."
            : "Tenant deletion failed. The tenant remains in the list.";
        string? correlation = response.Headers.TryGetValues("X-Correlation-Id", out var values) ? values.FirstOrDefault() : null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict && problem.TryGetProperty("message", out var message)) detail = message.GetString() ?? detail;
            if (problem.TryGetProperty("correlationId", out var reference)) correlation = reference.GetString();
            if (problem.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("correlationId", out var nested)) correlation = nested.GetString();
        }
        catch (System.Text.Json.JsonException) { }
        throw new HttpRequestException(detail + (string.IsNullOrWhiteSpace(correlation) ? "" : $" Reference: {correlation}."), null, response.StatusCode);

    }
}

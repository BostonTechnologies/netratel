using System.Net.Http.Json;
using System.Web;
using NetRatel.Shared.Contracts.Enrollment;

namespace NetRatel.Web.Services.Enrollment;

public sealed class EnrollmentCodeApiService : IEnrollmentCodeApiService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public EnrollmentCodeApiService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IReadOnlyList<EnrollmentCodeDto>> ListAsync(int tenantId, string? status, string? search = null, CancellationToken ct = default)
    {
        var q = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(status))
        {
            q["status"] = status;
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            q["search"] = search;
        }

        var client = _httpClientFactory.CreateClient("OrchestratorApi");
        var uri = $"/api/v1/tenants/{tenantId}/enrollment-codes?{q}";
        var response = await client.GetFromJsonAsync<EnrollmentCodeListResponse>(uri, ct);
        return response?.Items ?? [];
    }

    public async Task<IssuedEnrollmentCodeDto> IssueAsync(int tenantId, IssueEnrollmentCodeRequest request, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("OrchestratorApi");
        var response = await client.PostAsJsonAsync($"/api/v1/tenants/{tenantId}/enrollment-codes", request, ct);
        response.EnsureSuccessStatusCode();
        var issued = await response.Content.ReadFromJsonAsync<IssuedEnrollmentCodeDto>(cancellationToken: ct);
        if (issued is null)
        {
            throw new InvalidOperationException("Enrollment code response was empty.");
        }

        return issued;
    }

    public async Task RevokeAsync(int tenantId, Guid codeId, string? reason, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("OrchestratorApi");
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/enrollment-codes/{codeId}/revoke",
            new RevokeEnrollmentCodeRequest(reason),
            ct);
        response.EnsureSuccessStatusCode();
    }
}

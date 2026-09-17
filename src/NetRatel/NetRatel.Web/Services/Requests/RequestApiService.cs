using System.Net.Http.Json;
using NetRatel.Web.Models.Requests;

namespace NetRatel.Web.Services.Requests;

public class RequestApiService
{
    private readonly IHttpClientFactory _clientFactory;

    public RequestApiService(IHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    private HttpClient CreateClient() => _clientFactory.CreateClient("OrchestratorApi");

    public async Task<IReadOnlyList<RequestDto>> GetRequestsAsync(CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var result = await client.GetFromJsonAsync<List<RequestDto>>("/api/v1/requests", cancellationToken);
        return result ?? new List<RequestDto>();
    }

    public async Task UpdateRequestAsync(int id, UpdateRequestDto request, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var response = await client.PutAsJsonAsync($"/api/v1/requests/{id}", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

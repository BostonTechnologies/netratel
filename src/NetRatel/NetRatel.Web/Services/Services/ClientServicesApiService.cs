using System.Net;
using System.Net.Http.Json;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Web.Services.Services;

public interface IClientServicesApiService
{
    Task<ClientServicesReadModelDto?> GetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default);
    Task<ClientServicesRefreshResponse> RefreshAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default);
}

/// <summary>Reads cached evidence; only an explicit refresh requests client collection.</summary>
public sealed class ClientServicesApiService(IHttpClientFactory factory) : IClientServicesApiService
{
    private readonly HttpClient _http = factory.CreateClient("OrchestratorApi");

    public async Task<ClientServicesReadModelDto?> GetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await _http.GetAsync($"/api/v2/agents/{tenantId}/{agentId:D}/services", timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ClientServicesReadModelDto>(cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("The services read timed out."); }
    }

    public async Task<ClientServicesRefreshResponse> RefreshAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await _http.PostAsync($"/api/v2/agents/{tenantId}/{agentId:D}/services/refresh", null, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ClientServicesRefreshResponse>(cancellationToken: timeout.Token).ConfigureAwait(false)
                ?? throw new HttpRequestException("The services refresh response was empty.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("The services refresh admission timed out."); }
    }
}

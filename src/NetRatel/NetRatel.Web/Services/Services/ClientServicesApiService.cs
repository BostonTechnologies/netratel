using System.Net;
using System.Net.Http.Json;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Web.Services.Services;

public interface IClientServicesApiService
{
    Task<ClientServicesReadModelDto?> GetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default);
    Task<ClientServicesRefreshResponse> RefreshAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default);
    Task<ClientServiceOperationDto> ControlAsync(int tenantId, Guid agentId, ClientServiceControlRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Service control is unavailable.");
    Task<ClientServiceOperationDto> GetOperationAsync(int tenantId, Guid agentId, Guid operationId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Service control is unavailable.");
}

/// <summary>Reads cached evidence; the dialog requests fresh collection through the refresh API.</summary>
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

    public async Task<ClientServiceOperationDto> ControlAsync(int tenantId, Guid agentId, ClientServiceControlRequest request, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await _http.PostAsJsonAsync($"/api/v2/agents/{tenantId}/{agentId:D}/services/control", request, timeout.Token).ConfigureAwait(false);
            await EnsureControlSuccessAsync(response, timeout.Token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<ClientServiceOperationDto>(cancellationToken: timeout.Token).ConfigureAwait(false)
                ?? throw new HttpRequestException("The service operation response was empty.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("Service operation admission could not be confirmed."); }
    }

    public async Task<ClientServiceOperationDto> GetOperationAsync(int tenantId, Guid agentId, Guid operationId, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await _http.GetAsync($"/api/v2/agents/{tenantId}/{agentId:D}/services/operations/{operationId:D}", timeout.Token).ConfigureAwait(false);
            await EnsureControlSuccessAsync(response, timeout.Token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<ClientServiceOperationDto>(cancellationToken: timeout.Token).ConfigureAwait(false)
                ?? throw new HttpRequestException("The service operation response was empty.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("The service operation read timed out."); }
    }

    private static async Task EnsureControlSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);
            var code = problem.TryGetProperty("code", out var value) ? value.GetString() : null;
            throw new ClientServiceControlApiException(code ?? "service_operation_rejected");
        }
        response.EnsureSuccessStatusCode();
    }
}

public sealed class ClientServiceControlApiException(string code) : HttpRequestException("The service operation was rejected.")
{
    public string Code { get; } = code;
}

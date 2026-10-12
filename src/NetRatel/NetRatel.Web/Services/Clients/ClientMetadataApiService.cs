using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.Contracts;

namespace NetRatel.Web.Services.Clients;

public interface IClientMetadataApiService
{
    Task<ClientMetadataDto?> GetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default);
    Task<ClientMetadataDto> UpdateAsync(int tenantId, Guid agentId, UpdateClientMetadataRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ClientMetadataApiService(IHttpClientFactory factory) : IClientMetadataApiService
{
    private readonly HttpClient _http = factory.CreateClient("OrchestratorApi");

    public async Task<ClientMetadataDto?> GetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _http.GetAsync(Path(tenantId, agentId), timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, tenantId, agentId, timeout.Token).ConfigureAwait(false);
    }

    public async Task<ClientMetadataDto> UpdateAsync(int tenantId, Guid agentId, UpdateClientMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _http.PutAsJsonAsync(Path(tenantId, agentId), request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, tenantId, agentId, timeout.Token).ConfigureAwait(false);
    }

    private static async Task<ClientMetadataDto> ReadAsync(HttpResponseMessage response, int tenantId, Guid agentId,
        CancellationToken cancellationToken)
    {
        var metadata = await response.Content.ReadFromJsonAsync<ClientMetadataDto>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (metadata is null || metadata.TenantId != tenantId || metadata.AgentId != agentId || metadata.Tags is null ||
            !ClientMetadataValidation.TryNormalize(new(metadata.FriendlyName, metadata.Tags), out _, out _))
            throw new JsonException("The metadata response did not match the selected client.");
        return metadata;
    }

    private static string Path(int tenantId, Guid agentId) => $"/api/v2/agents/{tenantId}/{agentId:D}/metadata";
}

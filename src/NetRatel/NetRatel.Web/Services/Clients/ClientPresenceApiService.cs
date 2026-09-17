using System.Net;
using System.Net.Http.Json;
using NetRatel.Shared.Contracts;

namespace NetRatel.Web.Services.Clients;

/// <summary>
/// Reads the opt-in Akka gateway presence canary. It is intentionally separate
/// from ClientApiService because its records are not legacy ClientDto values.
/// </summary>
public sealed class ClientPresenceApiService(IHttpClientFactory clientFactory)
{
    public async Task<ClientPresenceListDto?> GetGatewayPresenceAsync(bool onlineOnly = false, CancellationToken cancellationToken = default)
    {
        var client = clientFactory.CreateClient("OrchestratorApi");
        var path = onlineOnly ? "/api/v2/client-presence?online=true" : "/api/v2/client-presence";
        using var response = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<ClientPresenceListDto>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}

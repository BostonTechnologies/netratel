using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.Contracts;

namespace NetRatel.Web.Services.Clients;

/// <summary>Reads the authoritative, Agent-ID keyed client directory.</summary>
public sealed class ClientPresenceApiService(IHttpClientFactory clientFactory)
{
    public async Task<ClientPresenceListDto> GetDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var client = clientFactory.CreateClient("OrchestratorApi");
        using var response = await client.GetAsync("/api/v2/client-presence", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var directory = await response.Content
            .ReadFromJsonAsync<ClientPresenceListDto>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new System.Text.Json.JsonException("The client directory API returned an empty response.");

        if (string.IsNullOrWhiteSpace(directory.Mode) || directory.Items is null)
        {
            throw new JsonException("The client directory API returned an incomplete directory shape.");
        }

        foreach (var entry in directory.Items)
        {
            if (entry is null
                || entry.TenantId <= 0
                || entry.AgentId == Guid.Empty
                || string.IsNullOrWhiteSpace(entry.PresenceId)
                || string.IsNullOrWhiteSpace(entry.DisplayName)
                || entry.Capabilities is null
                || string.IsNullOrWhiteSpace(entry.Source)
                || string.IsNullOrWhiteSpace(entry.Authority))
            {
                throw new JsonException("The client directory API returned an incomplete client record.");
            }
        }

        return directory;
    }
}

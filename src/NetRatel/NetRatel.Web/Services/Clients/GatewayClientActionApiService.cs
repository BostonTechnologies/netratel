using System.Net.Http.Json;

namespace NetRatel.Web.Services.Clients;

/// <summary>Invokes Agent-ID keyed control actions on the authoritative gateway surface.</summary>
public sealed class GatewayClientActionApiService(IHttpClientFactory clientFactory)
{
    public async Task<GatewayPingResult> PingAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        var client = clientFactory.CreateClient("OrchestratorApi");
        using var response = await client.PostAsync($"/api/v2/agents/{tenantId}/{agentId:D}/ping", content: null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<GatewayPingResult>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The gateway returned an empty ping response.");
    }
}

public sealed record GatewayPingResult(
    int TenantId,
    Guid AgentId,
    Guid RequestId,
    DateTimeOffset SentAtUtc,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset AgentRespondedAtUtc,
    double RoundTripMilliseconds,
    string Authority);

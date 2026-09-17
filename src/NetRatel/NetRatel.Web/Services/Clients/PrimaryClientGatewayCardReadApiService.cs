using System.Net;
using System.Net.Http.Json;
using NetRatel.Shared.Contracts;

namespace NetRatel.Web.Services.Clients;

/// <summary>
/// Reads the explicit primary-client to gateway-agent binding projection. A
/// missing projection is not inferred from host names or client identities.
/// </summary>
public sealed class PrimaryClientGatewayCardReadApiService(IHttpClientFactory clientFactory)
{
    public async Task<IReadOnlyList<PrimaryClientGatewayCardReadDto>> ListAsync(
        int tenantId,
        CancellationToken cancellationToken = default)
    {
        var client = clientFactory.CreateClient("OrchestratorApi");
        using var response = await client.GetAsync(
            $"/api/v2/tenants/{tenantId}/primary-client-cards/gateway",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<PrimaryClientGatewayCardReadDto>();
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<PrimaryClientGatewayCardReadListDto>(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload?.Items ?? Array.Empty<PrimaryClientGatewayCardReadDto>();
    }
}

public sealed record PrimaryClientGatewayCardReadListDto(
    int TenantId,
    long Revision,
    string Source,
    IReadOnlyList<PrimaryClientGatewayCardReadDto> Items);

public sealed record PrimaryClientGatewayCardReadDto(
    string PrimaryClientIdentity,
    int TenantId,
    Guid AgentId,
    Guid BindingId,
    bool Online,
    DateTimeOffset? LastHeartbeatAtUtc,
    string? AgentVersion,
    IReadOnlyList<string> Capabilities,
    PrimaryClientGatewayCardFieldSourceDto Presence,
    PrimaryClientGatewayCardTelemetryDto? Telemetry,
    PrimaryClientGatewayCardLatencyDto Latency,
    long Revision,
    GatewayTerminalCapabilityDto? Terminal = null);

public sealed record PrimaryClientGatewayCardFieldSourceDto(string Authority, bool IsAuthoritative);

public sealed record PrimaryClientGatewayCardTelemetryDto(
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    double? CpuUsagePercent,
    double? MemoryUsagePercent,
    string Authority,
    bool IsAuthoritative);

public sealed record PrimaryClientGatewayCardLatencyDto(
    double? RoundTripMilliseconds,
    DateTimeOffset? MeasuredAtUtc,
    string Authority,
    bool IsAuthoritative);

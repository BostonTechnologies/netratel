using NetRatel.Web.Models.Clients;
using NetRatel.Web.Services.Telemetry;
using System.Net.Http;
using System.Text.Json;

namespace NetRatel.Web.Services.Clients;

/// <summary>Composes V2 directory and telemetry snapshots for the Clients page.</summary>
public sealed class ClientPresentationService(
    ClientPresenceApiService presenceApi,
    GatewayTelemetryApiService telemetryApi,
    ILogger<ClientPresentationService> logger)
{
    public async Task<ClientPresentationLoadResult> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        var presence = await presenceApi.GetGatewayPresenceAsync(onlineOnly: false, cancellationToken).ConfigureAwait(false);
        if (presence is null)
        {
            return ClientPresentationLoadResult.Unavailable;
        }

        var telemetryByAgent = (await GetTelemetryOrEmptyAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(snapshot => (snapshot.TenantId, snapshot.AgentId))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.ReceivedAtUtc).First());

        var clients = presence.Items
            .Select(client => new ClientPresentationModel(
                client.TenantId,
                client.AgentId,
                client.DisplayName,
                client.HostName ?? client.DisplayName,
                client.TenantName ?? $"Tenant {client.TenantId}",
                client.OperatingSystem,
                client.Architecture,
                client.Online,
                client.IsEnabled,
                client.LastReceivedAtUtc,
                client.AgentVersion,
                client.Capabilities,
                client.Terminal,
                telemetryByAgent.GetValueOrDefault((client.TenantId, client.AgentId)),
                client.File))
            .OrderBy(client => client.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(client => client.TenantId)
            .ThenBy(client => client.AgentId)
            .ToArray();

        return new ClientPresentationLoadResult(true, clients);
    }

    private async Task<IReadOnlyList<GatewayTelemetrySummary>> GetTelemetryOrEmptyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await telemetryApi.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Gateway telemetry enrichment is unavailable; rendering the authoritative client directory without telemetry.");
            return [];
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Gateway telemetry enrichment returned an invalid payload; rendering the authoritative client directory without telemetry.");
            return [];
        }
    }
}

public sealed record ClientPresentationLoadResult(bool IsAvailable, IReadOnlyList<ClientPresentationModel> Clients)
{
    public static ClientPresentationLoadResult Unavailable { get; } = new(false, []);
}

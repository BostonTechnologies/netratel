using System.Net;
using System.Net.Http;
using System.Text.Json;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Models.Clients;
using NetRatel.Web.Services.Authentication;
using NetRatel.Web.Services.Telemetry;

namespace NetRatel.Web.Services.Clients;

/// <summary>Composes V2 directory and telemetry snapshots for the Clients page.</summary>
public sealed class ClientPresentationService(
    ClientPresenceApiService presenceApi,
    GatewayTelemetryApiService telemetryApi,
    ILogger<ClientPresentationService> logger)
{
    private static readonly TimeSpan DirectoryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TelemetryTimeout = TimeSpan.FromSeconds(5);

    public async Task<ClientPresentationLoadResult> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        ClientPresenceListDto presence;
        using (var directoryRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            directoryRequest.CancelAfter(DirectoryTimeout);
            try
            {
                presence = await presenceApi.GetDirectoryAsync(directoryRequest.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ClientPresentationLoadResult.Unavailable(new(ClientPresentationFailureKind.Timeout));
            }
            catch (ReauthRequiredException)
            {
                logger.LogWarning("The authoritative client directory request requires a fresh operator session.");
                return ClientPresentationLoadResult.Unavailable(new(ClientPresentationFailureKind.AuthenticationRequired, HttpStatusCode.Unauthorized));
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "The authoritative client directory request failed with status {StatusCode}.", exception.StatusCode);
                return ClientPresentationLoadResult.Unavailable(Classify(exception.StatusCode));
            }
            catch (TimeoutException exception)
            {
                logger.LogWarning(exception, "The authoritative client directory request timed out.");
                return ClientPresentationLoadResult.Unavailable(new(ClientPresentationFailureKind.Timeout));
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "The authoritative client directory returned an invalid payload.");
                return ClientPresentationLoadResult.Unavailable(new(ClientPresentationFailureKind.InvalidResponse));
            }
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
                client.File,
                client.LatencyMilliseconds,
                client.LatencyMeasuredAtUtc,
                client.LatencyExpiresAtUtc))
            .OrderBy(client => client.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(client => client.TenantId)
            .ThenBy(client => client.AgentId)
            .ToArray();

        return new ClientPresentationLoadResult(true, clients);
    }

    private async Task<IReadOnlyList<GatewayTelemetrySummary>> GetTelemetryOrEmptyAsync(CancellationToken cancellationToken)
    {
        using var telemetryRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        telemetryRequest.CancelAfter(TelemetryTimeout);
        try
        {
            var telemetry = await telemetryApi.GetOverviewAsync(telemetryRequest.Token).ConfigureAwait(false);
            if (telemetry.Any(summary => summary is null
                || summary.TenantId <= 0
                || summary.AgentId == Guid.Empty
                || summary.Disks is null
                || summary.Disks.Any(disk => disk is null)
                || summary.Networks is null
                || summary.Networks.Any(network => network is null)))
            {
                throw new JsonException("The telemetry API returned an incomplete telemetry record.");
            }

            return telemetry;
        }
        catch (ReauthRequiredException)
        {
            logger.LogWarning("Gateway telemetry enrichment needs a fresh operator session; rendering the authoritative directory without telemetry.");
            return [];
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Gateway telemetry enrichment timed out; rendering the authoritative client directory without telemetry.");
            return [];
        }
        catch (TimeoutException exception)
        {
            logger.LogWarning(exception, "Gateway telemetry enrichment timed out; rendering the authoritative client directory without telemetry.");
            return [];
        }
    }

    private static ClientPresentationFailure Classify(HttpStatusCode? statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => new(ClientPresentationFailureKind.AuthenticationRequired, statusCode),
        HttpStatusCode.Forbidden => new(ClientPresentationFailureKind.PermissionDenied, statusCode),
        HttpStatusCode.NotFound => new(ClientPresentationFailureKind.RouteUnavailable, statusCode),
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => new(ClientPresentationFailureKind.Timeout, statusCode),
        { } status when (int)status >= 500 => new(ClientPresentationFailureKind.BackendUnavailable, status),
        { } status => new(ClientPresentationFailureKind.RequestRejected, status),
        null => new(ClientPresentationFailureKind.NetworkUnavailable)
    };
}

public sealed record ClientPresentationLoadResult(
    bool IsAvailable,
    IReadOnlyList<ClientPresentationModel> Clients,
    ClientPresentationFailure? Failure = null)
{
    public static ClientPresentationLoadResult Unavailable(ClientPresentationFailure failure) => new(false, [], failure);
}

public sealed record ClientPresentationFailure(ClientPresentationFailureKind Kind, HttpStatusCode? StatusCode = null);

public enum ClientPresentationFailureKind
{
    AuthenticationRequired,
    PermissionDenied,
    RouteUnavailable,
    Timeout,
    BackendUnavailable,
    NetworkUnavailable,
    InvalidResponse,
    RequestRejected,
    Unknown
}

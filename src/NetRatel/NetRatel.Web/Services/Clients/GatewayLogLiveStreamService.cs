using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Services.Authentication;

namespace NetRatel.Web.Services.Clients;

public interface IGatewayLogLiveStreamService
{
    Task<GatewayLogLiveSubscription> SubscribeAsync(
        int tenantId,
        Guid agentId,
        string sourceId,
        Func<GatewayLogBatchDto, Task> onBatch,
        CancellationToken cancellationToken = default,
        GatewayLogQueryFilters? filters = null);
}

/// <summary>
/// Creates an authenticated, cancellable live-log subscription per explorer.
/// A dialog owns the returned subscription so closing or changing source tears
/// down its hub work deterministically.
/// </summary>
public sealed class GatewayLogLiveStreamService(
    IHttpClientFactory clientFactory,
    ITokenService tokens,
    ILogger<GatewayLogLiveStreamService> logger) : IGatewayLogLiveStreamService
{
    public async Task<GatewayLogLiveSubscription> SubscribeAsync(
        int tenantId,
        Guid agentId,
        string sourceId,
        Func<GatewayLogBatchDto, Task> onBatch,
        CancellationToken cancellationToken = default,
        GatewayLogQueryFilters? filters = null)
    {
        var http = clientFactory.CreateClient("OrchestratorApi");
        if (http.BaseAddress is null) throw new InvalidOperationException("The OrchestratorApi base address is not configured.");

        // Resolve the token while the circuit is active. The SignalR transport
        // invokes its token callback from its background connection loop, where
        // HttpContext is not guaranteed to be available.
        var accessToken = await tokens.GetValidAccessTokenAsync().ConfigureAwait(false);

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(http.BaseAddress, "/hubs/operations"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .WithAutomaticReconnect()
            .Build();
        connection.On<GatewayLogBatchDto>("LogBatch", onBatch);
        connection.Reconnected += async _ =>
        {
            try
            {
                var page = await connection.InvokeAsync<GatewayLogPageDto>(
                    "SubscribeLogs", tenantId, agentId.ToString("D"), sourceId, null, filters, CancellationToken.None).ConfigureAwait(false);
                await onBatch(new GatewayLogBatchDto(
                    tenantId,
                    agentId,
                    "reconnected",
                    page.Records,
                    page.NextCursor,
                    page.DroppedRecordCount,
                    page.ResyncRequired)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Keep diagnostics structured and do not write user log data.
                logger.LogWarning("Operations log hub reconnect did not resubscribe. FailureKind={FailureKind}", FailureKind(exception));
            }
        };

        var stage = "starting-connection";
        try
        {
            await connection.StartAsync(cancellationToken).ConfigureAwait(false);
            stage = "subscribing";
            var initial = await connection.InvokeAsync<GatewayLogPageDto>(
                "SubscribeLogs", tenantId, agentId.ToString("D"), sourceId, null, filters, cancellationToken).ConfigureAwait(false);
            return new GatewayLogLiveSubscription(connection, tenantId, agentId, sourceId, initial);
        }
        catch (Exception exception)
        {
            // This intentionally excludes exception text and user log payloads.
            // Stage/site are enough to classify a transport regression safely.
            logger.LogWarning(
                "Operations log hub subscription could not be established. Stage={Stage} FailureKind={FailureKind} FailureSite={FailureSite}",
                stage,
                FailureKind(exception),
                FailureSite(exception));
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string FailureKind(Exception exception)
    {
        if (string.Equals(exception.GetType().Name, "HubException", StringComparison.Ordinal))
        {
            if (exception.Message.Contains("connection capacity", StringComparison.OrdinalIgnoreCase)) return "hub-connection-capacity";
            if (exception.Message.Contains("subscription limit", StringComparison.OrdinalIgnoreCase)) return "hub-subscription-limit";
            if (exception.Message.Contains("source is unavailable", StringComparison.OrdinalIgnoreCase)) return "log-source-unavailable";
            if (exception.Message.Contains("tenant authorization", StringComparison.OrdinalIgnoreCase)) return "tenant-authorization";
        }

        return exception switch
        {
            HttpRequestException => "http-request",
            OperationCanceledException => "operation-cancelled",
            _ => exception.GetType().Name
        };
    }

    private static string FailureSite(Exception exception)
    {
        var method = exception.TargetSite;
        return method?.DeclaringType is { FullName: { } typeName }
            ? $"{typeName}.{method.Name}"
            : "unknown";
    }
}

public sealed class GatewayLogLiveSubscription : IAsyncDisposable
{
    private static readonly TimeSpan UnsubscribeTimeout = TimeSpan.FromMilliseconds(750);
    private readonly Func<ValueTask> _disposeAsync;
    private int _disposed;

    internal GatewayLogLiveSubscription(
        HubConnection connection,
        int tenantId,
        Guid agentId,
        string sourceId,
        GatewayLogPageDto initialPage)
        : this(initialPage, async () =>
        {
            try
            {
                if (connection.State == HubConnectionState.Connected)
                {
                    try
                    {
                        await connection.InvokeAsync("UnsubscribeLogs", tenantId, agentId.ToString("D"), sourceId, CancellationToken.None)
                            .WaitAsync(UnsubscribeTimeout).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        System.Diagnostics.Trace.TraceWarning("Timed out while unsubscribing an operations log source; the hub disconnect cleanup will finish it.");
                    }
                }
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        })
    {
    }

    public GatewayLogLiveSubscription(GatewayLogPageDto initialPage, Func<ValueTask> disposeAsync)
    {
        InitialPage = initialPage;
        _disposeAsync = disposeAsync;
    }

    public GatewayLogPageDto InitialPage { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _disposeAsync().ConfigureAwait(false);
    }
}

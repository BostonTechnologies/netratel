using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using NetRatel.Application.Fanout;
using NetRatel.Web.Services.Authentication;

namespace NetRatel.Web.Services.Terminal;

/// <summary>
/// Maintains the web application's authenticated subscription to the DEV
/// authority hub. Terminal bytes remain on the dedicated SSE data stream;
/// this hub carries bounded lifecycle/projection metadata only.
/// </summary>
public sealed class AkkaAuthorityFanoutClient(
    IHttpClientFactory clientFactory,
    ITokenService tokens,
    ILogger<AkkaAuthorityFanoutClient> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<TerminalTarget, byte> _terminalTargets = new();
    private HubConnection? _connection;

    public async Task<IAsyncDisposable> SubscribeTerminalAsync(int tenantId, Guid agentId, string sessionId, CancellationToken cancellationToken = default)
    {
        var target = new TerminalTarget(tenantId, agentId, sessionId);
        var connection = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await SubscribeAsync(connection, target, cancellationToken).ConfigureAwait(false);
        _terminalTargets.TryAdd(target, 0);
        return new TerminalSubscription(this, target);
    }

    private async Task<HubConnection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { State: HubConnectionState.Connected } connected) return connected;
            if (_connection is null)
            {
                var http = clientFactory.CreateClient("OrchestratorApi");
                if (http.BaseAddress is null) throw new InvalidOperationException("The OrchestratorApi base address is not configured.");
                _connection = new HubConnectionBuilder()
                    .WithUrl(new Uri(http.BaseAddress, "/hubs/akka-authority"), options =>
                    {
                        options.AccessTokenProvider = async () => await tokens.GetValidAccessTokenAsync().ConfigureAwait(false);
                    })
                    .WithAutomaticReconnect()
                    .Build();
                _connection.On<ShadowFanoutEnvelope>("shadowUpdated", envelope =>
                {
                    if (envelope.IsAuthoritative)
                        logger.LogDebug("Akka authority fanout event received category={Category} target={Target} status={Status}", envelope.Category, envelope.Target.Scope, envelope.Status);
                });
                _connection.Reconnected += ResubscribeAsync;
            }

            await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResubscribeAsync(string? _)
    {
        var connection = _connection;
        if (connection is null) return;
        foreach (var target in _terminalTargets.Keys)
        {
            try { await SubscribeAsync(connection, target, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) { logger.LogWarning(exception, "Akka authority terminal subscription could not be restored session={SessionId}", target.SessionId); }
        }
    }

    private static Task SubscribeAsync(HubConnection connection, TerminalTarget target, CancellationToken cancellationToken) =>
        connection.InvokeAsync<IReadOnlyList<ShadowFanoutEnvelope>>("SubscribeTerminal", cancellationToken, target.TenantId, target.AgentId.ToString("D"), target.SessionId);

    private async ValueTask UnsubscribeAsync(TerminalTarget target)
    {
        _terminalTargets.TryRemove(target, out _);
        if (_connection is not { State: HubConnectionState.Connected } connection) return;
        await connection.InvokeAsync("UnsubscribeTerminal", CancellationToken.None, target.TenantId, target.AgentId.ToString("D"), target.SessionId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private sealed record TerminalTarget(int TenantId, Guid AgentId, string SessionId);

    private sealed class TerminalSubscription(AkkaAuthorityFanoutClient owner, TerminalTarget target) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) await owner.UnsubscribeAsync(target).ConfigureAwait(false);
        }
    }
}

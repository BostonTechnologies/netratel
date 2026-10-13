using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using NetRatel.Web.Services.Authentication;

namespace NetRatel.Web.Services;

public sealed record ClientUpdateActivityEvent(string Kind, Guid? Id, bool Connected);

public interface IClientUpdateActivityStream : IAsyncDisposable
{
    Task StartAsync(int? tenantId, Func<ClientUpdateActivityEvent, Task> onNotification,
        Action<string> onStatus, CancellationToken ct = default);
    Task StopAsync();
}

/// <summary>Activity owns one stream and reconnects through the existing authenticated transport.</summary>
public sealed class ClientUpdateActivityStream(SseClient transport, AuthenticationStateProvider authentication) : IClientUpdateActivityStream
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _running;
    private Action<string>? _onStatus;
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<Guid> _recent = [];
    private bool _disposed;
    private bool _authenticationSubscribed;

    public async Task StartAsync(int? tenantId, Func<ClientUpdateActivityEvent, Task> onNotification,
        Action<string> onStatus, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            await StopCoreAsync();
            if (_disposed || ct.IsCancellationRequested) return;
            _onStatus = onStatus;
            _seen.Clear();
            _recent.Clear();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            authentication.AuthenticationStateChanged += AuthenticationChanged;
            _authenticationSubscribed = true;
            _running = RunAsync(tenantId, onNotification, onStatus, _cts.Token);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var cts = _cts;
        var running = _running;
        var status = _onStatus;
        _cts = null;
        _running = null;
        _onStatus = null;
        cts?.Cancel();
        if (running is not null)
        {
            try { await running.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cts?.Dispose();
        status?.Invoke("Paused");
        if (_authenticationSubscribed)
        {
            authentication.AuthenticationStateChanged -= AuthenticationChanged;
            _authenticationSubscribed = false;
        }
    }

    private void AuthenticationChanged(Task<AuthenticationState> _)
    {
        // Fail closed immediately on logout/account/scope replacement; navigation can start
        // a fresh subscription with the current circuit credential when appropriate.
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* A concurrent Stop already drained this session. */ }
        _onStatus?.Invoke("Paused");
    }

    private async Task RunAsync(int? tenantId, Func<ClientUpdateActivityEvent, Task> onNotification,
        Action<string> onStatus, CancellationToken ct)
    {
        var path = "/api/v1/client-updates/activity/stream" + (tenantId.HasValue ? $"?tenantId={tenantId.Value}" : string.Empty);
        var backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            onStatus("Reconnecting");
            try
            {
                await foreach (var json in transport.Connect(path, ct).ConfigureAwait(false))
                {
                    var notification = JsonSerializer.Deserialize<ClientUpdateActivityEvent>(json, JsonOptions);
                    if (notification is null) continue;
                    backoff = 1;
                    if (notification.Id is { } id)
                    {
                        if (!_seen.Add(id)) continue;
                        _recent.Enqueue(id);
                        if (_recent.Count > 256) _seen.Remove(_recent.Dequeue());
                    }
                    // The component claims Live only after the ready/reconnect snapshot succeeds.
                    if (!notification.Connected) onStatus("Reconnecting");
                    await onNotification(notification).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ReauthRequiredException) { onStatus("Paused"); return; }
            catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { onStatus("Paused"); return; }
            catch (Exception) when (!ct.IsCancellationRequested) { /* Reconnect obtains a new authoritative snapshot. */ }
            if (ct.IsCancellationRequested) break;
            onStatus("Reconnecting");
            await Task.Delay(TimeSpan.FromSeconds(backoff), ct).ConfigureAwait(false);
            backoff = Math.Min(30, backoff * 2);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopCoreAsync();
        }
        finally { _lifecycle.Release(); }
    }
}

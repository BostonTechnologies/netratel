using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using NetRatel.Web.Services.Authentication;

namespace NetRatel.Web.Services.ClientTasks;

public interface ITaskExecutionStream : IAsyncDisposable
{
    Task StartAsync(string requestId, Func<CancellationToken, Task<bool>> refresh,
        Action<string> onStatus, CancellationToken ct = default);
    Task StopAsync();
}

/// <summary>Reconnect notifications recover persisted task/output snapshots through the authenticated SSE transport.</summary>
public sealed class TaskExecutionStream(SseClient transport, AuthenticationStateProvider authentication) : ITaskExecutionStream
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _running;
    private Action<string>? _onStatus;
    private bool _disposed;

    public async Task StartAsync(string requestId, Func<CancellationToken, Task<bool>> refresh,
        Action<string> onStatus, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            await StopCoreAsync();
            if (_disposed || ct.IsCancellationRequested) return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _onStatus = onStatus;
            authentication.AuthenticationStateChanged += AuthenticationChanged;
            _running = RunAsync(requestId, refresh, onStatus, _cts.Token);
        }
        finally { _lifecycle.Release(); }
    }

    private void AuthenticationChanged(Task<AuthenticationState> _)
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _onStatus?.Invoke("Paused");
    }

    private async Task RunAsync(string requestId, Func<CancellationToken, Task<bool>> refresh,
        Action<string> onStatus, CancellationToken ct)
    {
        var delay = 1;
        while (!ct.IsCancellationRequested)
        {
            onStatus("Reconnecting");
            try
            {
                await foreach (var json in transport.Connect($"/api/v2/tasks/stream?requestId={Uri.EscapeDataString(requestId)}", ct).ConfigureAwait(false))
                {
                    using var notification = JsonDocument.Parse(json);
                    if (!notification.RootElement.TryGetProperty("requestId", out var id) || id.GetString() != requestId) continue;
                    var connected = notification.RootElement.TryGetProperty("connected", out var connection) && connection.ValueKind == JsonValueKind.True;
                    delay = 1;
                    // Every ready/change/heartbeat is a recovery opportunity. A log fetch failure
                    // changes the indicator and cannot terminate future task notifications.
                    var loaded = false;
                    try { loaded = await refresh(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception) { }
                    onStatus(!connected ? "Reconnecting" : loaded ? "Live" : "Recovering");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ReauthRequiredException) { onStatus("Paused"); return; }
            catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { onStatus("Paused"); return; }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            if (ct.IsCancellationRequested) break;
            onStatus("Reconnecting");
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            delay = Math.Min(delay * 2, 30);
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        authentication.AuthenticationStateChanged -= AuthenticationChanged;
        var cts = _cts;
        var running = _running;
        _cts = null;
        _running = null;
        cts?.Cancel();
        if (running is not null)
        {
            try { await running.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cts?.Dispose();
        _onStatus?.Invoke("Paused");
        _onStatus = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try { _disposed = true; await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }
}

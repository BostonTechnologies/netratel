using System.Text;
using System.Text.Json;
using NetRatel.Application.Notifications;

namespace NetRatel.Web.Services.Notifications;

public sealed class NetRatelNotificationStreamService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private bool _disposed;
    private bool _isConnected;

    public bool IsConnected => _isConnected;
    public event Action<bool>? ConnectionChanged;

    public NetRatelNotificationStreamService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task StartAsync(Func<NetRatelNotificationDto, Task> handler, string? tenantId = null, CancellationToken ct = default)
    {
        if (_disposed)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            if (_runningTask is not null && !_runningTask.IsCompleted)
                return;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runningTask = RunLoopAsync(handler, tenantId, _cts.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_disposed)
            return;

        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        var cts = _cts;
        var running = _runningTask;
        _cts = null;
        _runningTask = null;

        cts?.Cancel();
        if (running is not null)
        {
            try { await running; } catch { }
        }

        cts?.Dispose();
    }

    private async Task RunLoopAsync(Func<NetRatelNotificationDto, Task> handler, string? tenantId, CancellationToken ct)
    {
        var delayMs = 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndStreamAsync(handler, tenantId, ct);
                delayMs = 1000;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                SetConnected(false);
                await Task.Delay(delayMs, ct);
                delayMs = Math.Min(delayMs * 2, 30000);
            }
        }

        SetConnected(false);
    }

    private async Task ConnectAndStreamAsync(Func<NetRatelNotificationDto, Task> handler, string? tenantId, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("OrchestratorApi");
        var endpoint = "/api/v1/notifications/stream";
        if (!string.IsNullOrWhiteSpace(tenantId))
            endpoint += $"?tenantId={Uri.EscapeDataString(tenantId)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        SetConnected(true);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? eventName = null;
        var data = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                await DispatchAsync(eventName, data, handler);
                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
                continue;

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line[5..].Trim());
            }
        }

        SetConnected(false);
    }

    private void SetConnected(bool connected)
    {
        if (_isConnected == connected)
            return;

        _isConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    private static async Task DispatchAsync(
        string? eventName,
        StringBuilder payload,
        Func<NetRatelNotificationDto, Task> handler)
    {
        if (payload.Length == 0)
            return;

        if (!string.IsNullOrWhiteSpace(eventName) && !string.Equals(eventName, "notification", StringComparison.OrdinalIgnoreCase))
            return;

        var notification = JsonSerializer.Deserialize<NetRatelNotificationDto>(payload.ToString(), JsonOptions);
        if (notification is null)
            return;

        await handler(notification);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopCoreAsync();
        _gate.Dispose();
    }
}

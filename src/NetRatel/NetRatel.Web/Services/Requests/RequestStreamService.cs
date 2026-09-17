using System.Text;
using System.Text.Json;
using NetRatel.Web.Models.Requests;

namespace NetRatel.Web.Services.Requests;

public interface IRequestStreamService : IAsyncDisposable
{
    bool IsConnected { get; }
    event Action<bool>? ConnectionChanged;
    Task StartAsync(Func<RequestDto, Task> handler, CancellationToken ct = default);
    Task StopAsync();
}

public sealed class RequestStreamService : IRequestStreamService
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

    public RequestStreamService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task StartAsync(Func<RequestDto, Task> handler, CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_runningTask is not null && !_runningTask.IsCompleted)
            {
                return;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runningTask = RunLoopAsync(handler, _cts.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

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
        SetConnected(false);
    }

    private async Task RunLoopAsync(Func<RequestDto, Task> handler, CancellationToken ct)
    {
        var delayMs = 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndStreamAsync(handler, ct);
                delayMs = 1000;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                SetConnected(false);
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(delayMs, ct);
                delayMs = Math.Min(delayMs * 2, 30000);
            }
        }

        SetConnected(false);
    }

    private async Task ConnectAndStreamAsync(Func<RequestDto, Task> handler, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("OrchestratorApi");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/requests/stream");
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
            {
                break;
            }

            if (line.Length == 0)
            {
                await DispatchAsync(eventName, data, handler);
                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line[5..].Trim());
            }
        }

        SetConnected(false);
    }

    private void SetConnected(bool connected)
    {
        if (_isConnected == connected)
        {
            return;
        }

        _isConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    private static async Task DispatchAsync(
        string? eventName,
        StringBuilder payload,
        Func<RequestDto, Task> handler)
    {
        if (payload.Length == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(eventName) &&
            !string.Equals(eventName, "request-upsert", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var request = JsonSerializer.Deserialize<RequestDto>(payload.ToString(), JsonOptions);
        if (request is null)
        {
            return;
        }

        await handler(request);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopCoreAsync();
        _gate.Dispose();
    }
}

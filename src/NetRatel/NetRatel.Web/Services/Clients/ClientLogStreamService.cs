using System.Text.Json;
using NetRatel.Shared.Contracts;
using LegacySseClient = NetRatel.Web.Services.Sse.SseClient;
using LegacySseEvent = NetRatel.Web.Services.Sse.SseEvent;

namespace NetRatel.Web.Services.Clients;

public sealed class ClientLogStreamService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly LegacySseClient _sse;
    private Func<ClientLogEntryDto, Task>? _onLogEntry;

    public ClientLogStreamService(IHttpClientFactory factory, ITokenProvider tokens)
    {
        var http = factory.CreateClient("OrchestratorApi");
        _sse = new LegacySseClient(http, tokens.GetBearerAsync);
        _sse.OnEvent += OnSseAsync;
    }

    public async Task StartAsync(
        string clientIdentity,
        Func<ClientLogEntryDto, Task> onLogEntry,
        Func<Task>? onOpen = null,
        Func<Exception, Task>? onError = null,
        CancellationToken ct = default)
    {
        await StopAsync();
        _onLogEntry = onLogEntry;
        if (onOpen is not null)
        {
            _sse.OnOpen += () => _ = onOpen();
        }
        if (onError is not null)
        {
            _sse.OnError += ex => _ = onError(ex);
        }

        var http = (HttpClient)typeof(LegacySseClient)
            .GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(_sse)!;
        var path = $"/api/v1/clients/{Uri.EscapeDataString(clientIdentity)}/logs/stream";
        await _sse.StartAsync(new Uri(http.BaseAddress!, path.TrimStart('/')), ct);
    }

    public Task StopAsync() => _sse.StopAsync();

    private async Task OnSseAsync(LegacySseEvent ev)
    {
        if (!string.Equals(ev.Event, "client-log", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ev.Data))
        {
            return;
        }

        var dto = JsonSerializer.Deserialize<ClientLogEntryDto>(ev.Data, JsonOptions);
        if (dto is not null && _onLogEntry is not null)
        {
            await _onLogEntry(dto);
        }
    }

    public ValueTask DisposeAsync() => _sse.DisposeAsync();
}

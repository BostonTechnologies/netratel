using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Services;
using LegacySseClient = NetRatel.Web.Services.Sse.SseClient;
using LegacySseEvent = NetRatel.Web.Services.Sse.SseEvent;

namespace NetRatel.Web.Services.Telemetry;

public sealed class ClientTelemetryStreamService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly LegacySseClient _sse;
    private readonly Uri _baseAddress;
    private Func<AgentTelemetrySnapshotDto, Task>? _onTelemetry;

    public ClientTelemetryStreamService(IHttpClientFactory factory, ITokenProvider tokens)
    {
        var http = factory.CreateClient("OrchestratorApi");
        _baseAddress = http.BaseAddress!;
        _sse = new LegacySseClient(http, tokens.GetBearerAsync);
        _sse.OnEvent += OnSseAsync;
    }

    public async Task StartAsync(string clientIdentity, Func<AgentTelemetrySnapshotDto, Task> onTelemetry, CancellationToken ct = default)
    {
        await StopAsync();
        _onTelemetry = onTelemetry;
        var path = $"/api/v1/clients/{Uri.EscapeDataString(clientIdentity)}/telemetry/stream";
        await _sse.StartAsync(new Uri(_baseAddress, path), ct);
    }

    public Task StopAsync() => _sse.StopAsync();

    public async ValueTask DisposeAsync() => await _sse.DisposeAsync();

    private async Task OnSseAsync(LegacySseEvent ev)
    {
        if (!string.Equals(ev.Event, "telemetry", StringComparison.OrdinalIgnoreCase) || ev.Data is null || _onTelemetry is null)
        {
            return;
        }

        var dto = JsonSerializer.Deserialize<AgentTelemetrySnapshotDto>(ev.Data, JsonOptions);
        if (dto is not null)
        {
            await _onTelemetry(dto);
        }
    }
}

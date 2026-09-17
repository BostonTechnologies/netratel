using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Services;
using LegacySseClient = NetRatel.Web.Services.Sse.SseClient;
using LegacySseEvent = NetRatel.Web.Services.Sse.SseEvent;

namespace NetRatel.Web.Services.Telemetry;

public sealed class TelemetryOverviewStreamService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly LegacySseClient _sse;
    private readonly Uri _targetUri;
    private Func<AgentTelemetrySnapshotDto, Task>? _onTelemetry;

    public TelemetryOverviewStreamService(IHttpClientFactory factory, ITokenProvider tokens)
    {
        var http = factory.CreateClient("OrchestratorApi");
        _targetUri = new Uri(http.BaseAddress!, "/api/v1/telemetry/stream");
        _sse = new LegacySseClient(http, tokens.GetBearerAsync);
        _sse.OnEvent += OnSseAsync;
    }

    public async Task StartAsync(Func<AgentTelemetrySnapshotDto, Task> onTelemetry, CancellationToken ct = default)
    {
        await StopAsync();
        _onTelemetry = onTelemetry;
        await _sse.StartAsync(_targetUri, ct);
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

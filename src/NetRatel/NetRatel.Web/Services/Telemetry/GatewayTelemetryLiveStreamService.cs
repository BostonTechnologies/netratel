using System.Net.Http.Headers;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace NetRatel.Web.Services.Telemetry;

public sealed class GatewayTelemetryLiveStreamService(IHttpClientFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = factory.CreateClient("OrchestratorApi");

    public async IAsyncEnumerable<GatewayTelemetryLiveEvent> SubscribeAsync(
        int tenantId,
        Guid agentId,
        int samplePeriodMilliseconds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscriptionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<GatewayTelemetryLiveEvent>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        var producer = ProduceAsync(channel.Writer, tenantId, agentId, samplePeriodMilliseconds, subscriptionCancellation.Token);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(subscriptionCancellation.Token).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            subscriptionCancellation.Cancel();
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) when (subscriptionCancellation.IsCancellationRequested)
            {
                System.Diagnostics.Trace.WriteLine("Telemetry SSE producer stopped with its owning subscription.");
            }
        }
    }

    private async Task ProduceAsync(ChannelWriter<GatewayTelemetryLiveEvent> output, int tenantId, Guid agentId, int samplePeriodMilliseconds, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var hasConnected = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                output.TryWrite(GatewayTelemetryLiveEvent.Lifecycle(hasConnected ? "Reconnecting" : "Connecting"));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        $"/api/v2/agents/{tenantId}/{agentId:D}/telemetry/stream?samplePeriodMs={Math.Max(1000, samplePeriodMilliseconds)}");
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var reader = new StreamReader(stream);
                    string? eventType = null;
                    List<string>? data = null;
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                        if (line is null) break;
                        line = line.TrimStart();
                        if (line.StartsWith("event:", StringComparison.Ordinal)) eventType = line[6..].Trim();
                        else if (line.StartsWith("data:", StringComparison.Ordinal)) (data ??= []).Add(line[5..].TrimStart());
                        else if (line.Length == 0 && string.Equals(eventType, "telemetry", StringComparison.Ordinal) && data is not null)
                        {
                            var item = JsonSerializer.Deserialize<GatewayTelemetryLiveEvent>(string.Join('\n', data), JsonOptions);
                            if (item is not null) await output.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                            eventType = null;
                            data = null;
                            delay = TimeSpan.FromSeconds(1);
                            hasConnected = true;
                        }
                        else if (line.Length == 0)
                        {
                            eventType = null;
                            data = null;
                        }
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    System.Diagnostics.Trace.WriteLine($"Telemetry SSE connection failed ({exception.GetType().Name}). The subscription will reconnect.");
                }
                output.TryWrite(GatewayTelemetryLiveEvent.Lifecycle("Stale"));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output.TryComplete();
            return;
        }
        catch (Exception exception)
        {
            output.TryComplete(exception);
            return;
        }
        output.TryComplete();
    }

}

public sealed record GatewayTelemetryLiveEvent(
    string State,
    string ConnectionState,
    long? ConnectionEpoch,
    ulong? Sequence,
    GatewayTelemetrySummary? Snapshot,
    int EffectiveSamplePeriodMilliseconds,
    bool InteractiveRequested,
    bool InteractiveEffective,
    bool SupportsDynamicSampling,
    int RequestedSamplePeriodMilliseconds,
    string? AgentVersion,
    long PolicyRevision,
    DateTimeOffset PolicyExpiresAtUtc,
    string StateReason)
{
    public static GatewayTelemetryLiveEvent Lifecycle(string state) => new(
        state, state, null, null, null, 5000, false, false, false, 1000, null, 0,
        DateTimeOffset.MinValue, state.ToLowerInvariant());
}

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Web.Services.Services;

public sealed record ClientServicesLiveEvent(string State, ClientServicesReadModelDto? Snapshot = null);

public interface IClientServicesLiveStreamService
{
    IAsyncEnumerable<ClientServicesLiveEvent> SubscribeAsync(int tenantId, Guid agentId, CancellationToken cancellationToken);
}

/// <summary>One read-only, bounded SSE stream owned by an open Services viewer.</summary>
public sealed class ClientServicesLiveStreamService(IHttpClientFactory factory) : IClientServicesLiveStreamService
{
    private const int MaximumEventCharacters = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = factory.CreateClient("OrchestratorApiStreaming");

    public async IAsyncEnumerable<ClientServicesLiveEvent> SubscribeAsync(
        int tenantId, Guid agentId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return new ClientServicesLiveEvent("Connecting");
            HttpResponseMessage? response = null;
            var admissionDenied = false;
            try
            {
                using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                admission.CancelAfter(TimeSpan.FromSeconds(15));
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/agents/{tenantId}/{agentId:D}/services/events");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, admission.Token).ConfigureAwait(false);
                admissionDenied = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
                if (!admissionDenied) response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException)
            {
                response?.Dispose();
                response = null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                response = null;
            }

            if (admissionDenied)
            {
                response?.Dispose();
                yield return new ClientServicesLiveEvent("Unavailable");
                yield break;
            }

            if (response is not null)
            {
                using (response)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var reader = new StreamReader(stream);
                    await using var events = ReadEventsAsync(reader, cancellationToken).GetAsyncEnumerator(cancellationToken);
                    while (true)
                    {
                        var hasNext = false;
                        try { hasNext = await events.MoveNextAsync().ConfigureAwait(false); }
                        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException) { }
                        if (!hasNext) break;
                        var model = events.Current;
                        // A late or incorrectly routed event cannot cross the viewer's tenant/client fence.
                        if (model.TenantId != tenantId || model.AgentId != agentId) continue;
                        delay = TimeSpan.FromSeconds(1);
                        yield return new ClientServicesLiveEvent("Live", model);
                    }
                }
            }

            yield return new ClientServicesLiveEvent("Reconnecting");
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
        }
    }

    private static async IAsyncEnumerable<ClientServicesReadModelDto> ReadEventsAsync(
        StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? eventType = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > MaximumEventCharacters || data.Length + line.Length > MaximumEventCharacters)
                throw new InvalidDataException("The services event exceeded its bound.");
            if (line.StartsWith("event:", StringComparison.Ordinal)) eventType = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length != 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
            }
            else if (line.Length == 0)
            {
                if (eventType == "services" && data.Length != 0)
                {
                    var model = JsonSerializer.Deserialize<ClientServicesReadModelDto>(data.ToString(), JsonOptions);
                    if (model is not null && (model.LastCompleteInventory is null || model.LastCompleteInventory.Services is { Count: <= ClientServicesLimits.MaximumServices }) &&
                        model.WatchedServices is { Count: <= ClientServicesLimits.MaximumWatchServices } &&
                        model.MonitoredServiceNames is { Count: <= ClientServicesLimits.MaximumWatchServices })
                        yield return model;
                }
                eventType = null;
                data.Clear();
            }
        }
    }
}

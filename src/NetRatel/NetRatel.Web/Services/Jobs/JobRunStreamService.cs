using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Web.Services.Jobs;

public sealed record JobRunLiveUpdate(string State, JobRunStreamSnapshotDto? Snapshot = null);

public interface IJobRunStreamService
{
    IAsyncEnumerable<JobRunLiveUpdate> SubscribeAsync(ulong runId, CancellationToken cancellationToken);
}

/// <summary>An authenticated, read-only subscription owned by the open run page.</summary>
public sealed class JobRunStreamService(IHttpClientFactory factory) : IJobRunStreamService
{
    private const int MaximumEventCharacters = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = factory.CreateClient("OrchestratorApiStreaming");

    public async IAsyncEnumerable<JobRunLiveUpdate> SubscribeAsync(
        ulong runId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return new JobRunLiveUpdate("Connecting");
            HttpResponseMessage? response = null;
            var denied = false;
            try
            {
                using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                admission.CancelAfter(TimeSpan.FromSeconds(15));
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/jobruns/{runId}/stream");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, admission.Token).ConfigureAwait(false);
                denied = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
                if (!denied) response.EnsureSuccessStatusCode();
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                response = null;
            }
            if (denied)
            {
                response?.Dispose();
                yield return new JobRunLiveUpdate("Live updates unavailable.");
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
                        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidDataException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested) { }
                        if (!hasNext) break;
                        var message = events.Current;
                        if (message.Kind == "deleted")
                        {
                            yield return new JobRunLiveUpdate("Run no longer available.");
                            yield break;
                        }
                        if (message.Kind == "heartbeat")
                        {
                            yield return new JobRunLiveUpdate(message.Connected ? "Live" : "Reconnecting");
                            continue;
                        }
                        if (message.Snapshot is not { } snapshot || snapshot.Run.Id != runId) continue;
                        delay = TimeSpan.FromSeconds(1);
                        yield return new JobRunLiveUpdate(message.Connected ? "Live" : "Reconnecting", snapshot);
                        if (snapshot.Run.Status is JobRunStatusDto.Succeeded or JobRunStatusDto.Failed or JobRunStatusDto.Cancelled or JobRunStatusDto.TimedOut or JobRunStatusDto.Unknown)
                            yield break;
                    }
                }
            }
            yield return new JobRunLiveUpdate("Reconnecting");
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
        }
    }

    private static async IAsyncEnumerable<Envelope> ReadEventsAsync(
        StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var data = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            var line = await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);
            if (line is null) yield break;
            if (line.Length > MaximumEventCharacters || data.Length + line.Length > MaximumEventCharacters)
                throw new InvalidDataException("The run event exceeded its bound.");
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
            }
            else if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var message = JsonSerializer.Deserialize<Envelope>(data.ToString(), JsonOptions);
                    if (message is not null) yield return message;
                }
                data.Clear();
            }
        }
    }

    private sealed record Envelope(string Kind, string? Id, JobRunStreamSnapshotDto? Snapshot, bool Connected);
}

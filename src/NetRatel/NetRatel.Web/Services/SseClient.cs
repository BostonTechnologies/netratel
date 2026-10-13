using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace NetRatel.Web.Services;

/// <summary>The registered authenticated HTTP SSE transport. Consuming a stream owns its response.</summary>
public sealed class SseClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private CancellationTokenSource? _connection;

    public SseClient(HttpClient http)
    {
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async IAsyncEnumerable<string> Connect(string relativeUrl,
        [EnumeratorCancellation] CancellationToken externalToken = default)
    {
        var connection = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var previous = Interlocked.Exchange(ref _connection, connection);
        CancelSafely(previous);
        var ct = connection.Token;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!ct.IsCancellationRequested)
            {
                // Servers send a heartbeat within 15 seconds. A stalled proxy/socket is a
                // disconnect, not a Live connection. No unbounded background channel exists.
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                var line = await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);
                if (line is null) yield break;
                if (line.Length > 64 * 1024) throw new IOException("SSE notification exceeds the bounded payload limit.");
                if (line.StartsWith("data:", StringComparison.Ordinal)) yield return line[5..].TrimStart();
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _connection, null, connection);
            connection.Dispose();
        }
    }

    private static void CancelSafely(CancellationTokenSource? source)
    {
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { /* A completing enumeration already released the source. */ }
    }

    public ValueTask DisposeAsync()
    {
        CancelSafely(Interlocked.Exchange(ref _connection, null));
        return ValueTask.CompletedTask;
    }
}

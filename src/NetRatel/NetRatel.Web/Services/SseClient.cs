using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NetRatel.Web.Services;

public sealed class SseClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;
    private Task? _reader;
    private Channel<string> _lines = Channel.CreateUnbounded<string>();

    public SseClient(HttpClient http)
    {
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public IAsyncEnumerable<string> Connect(string relativeUrl, CancellationToken externalToken = default)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var ct = _cts.Token;
        _lines = Channel.CreateUnbounded<string>();
        var lines = _lines;

        _reader = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var sr = new StreamReader(stream, Encoding.UTF8);

                while (!ct.IsCancellationRequested)
                {
                    var line = await sr.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) break;

                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        var json = line.Substring(5).TrimStart();
                        await lines.Writer.WriteAsync(json, ct).ConfigureAwait(false);
                    }
                    // ignore comments ":" and empty separator lines
                }
            }
            catch (OperationCanceledException)
            {
                // normal on close/hover out
            }
            catch (IOException)
            {
                // normal on server/client closing the socket
            }
            catch (ObjectDisposedException)
            {
                // normal when NetworkStream is torn down under cancellation
            }
            catch (Exception)
            {
                // swallow other transient errors; callers can reconnect on demand
            }
            finally
            {
                lines.Writer.TryComplete();
            }
        }, ct);

        return ReadAllAsync(lines, ct);
    }

    private static async IAsyncEnumerable<string> ReadAllAsync(
        Channel<string> lines,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await lines.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (lines.Reader.TryRead(out var s))
            {
                yield return s;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_reader is not null) await _reader.ConfigureAwait(false); } catch { }
        _cts?.Dispose();
    }
}

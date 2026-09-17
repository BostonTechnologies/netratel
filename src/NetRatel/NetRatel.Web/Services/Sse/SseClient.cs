using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace NetRatel.Web.Services.Sse;

public sealed class SseClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string?>> _getBearer;
    private readonly TimeSpan _maxBackoff = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _initialBackoff = TimeSpan.FromSeconds(1);
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public event Func<SseEvent, Task>? OnEvent;
    public event Action? OnOpen;
    public event Action? OnClose;
    public event Action<Exception>? OnError;

    public SseClient(HttpClient http, Func<CancellationToken, Task<string?>> getBearer)
    {
        _http = http;
        _getBearer = getBearer;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task StartAsync(Uri url, CancellationToken ct = default)
    {
        await StopAsync();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pump = Task.Run(() => PumpAsync(url, _cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_pump is not null)
            {
                await Task.WhenAny(_pump, Task.Delay(500));
            }
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _pump = null;
        }
    }

    private async Task PumpAsync(Uri url, CancellationToken ct)
    {
        var backoff = _initialBackoff;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = new Version(1, 1),
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                req.Headers.Accept.ParseAdd("text/event-stream");
                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                var bearer = await _getBearer(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(bearer))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                OnOpen?.Invoke();

                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192);

                string? line;
                string? ev = null;
                var data = new StringBuilder();
                backoff = _initialBackoff;

                while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    if (line.Length == 0)
                    {
                        if (data.Length > 0 || ev is not null)
                        {
                            var payload = data.Length == 0 ? null : data.ToString();
                            if (OnEvent is not null)
                            {
                                await OnEvent(new SseEvent(ev, payload)).ConfigureAwait(false);
                            }
                        }

                        ev = null;
                        data.Clear();
                        continue;
                    }

                    if (line.StartsWith(":", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                    {
                        ev = line[6..].Trim();
                    }
                    else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        var d = line.Length > 5 ? line[5..].TrimStart() : string.Empty;
                        if (data.Length > 0)
                        {
                            data.Append('\n');
                        }
                        data.Append(d);
                    }
                }

                OnClose?.Invoke();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                OnClose?.Invoke();
                break;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
                OnClose?.Invoke();
            }

            await Task.Delay(backoff, ct).ConfigureAwait(false);
            var next = backoff.TotalMilliseconds * 2;
            backoff = TimeSpan.FromMilliseconds(Math.Min(_maxBackoff.TotalMilliseconds, next));
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

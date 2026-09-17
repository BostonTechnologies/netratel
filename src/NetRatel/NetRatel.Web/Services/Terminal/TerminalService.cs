using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetRatel.Shared.Contracts.Terminals;
using NetRatel.Web.Services.Authentication;

namespace NetRatel.Web.Services.Terminal;

public interface ITerminalService
{
    Task EnsureSubscribedAsync(CancellationToken ct = default);
    Task<TerminalOpenResponse> OpenSessionAsync(string clientIdentityHex, OpenTerminalRequest request, CancellationToken ct = default);
    Task<TerminalOpenResponse> OpenGatewaySessionAsync(int tenantId, Guid agentId, OpenTerminalRequest request, CancellationToken ct = default);
    Task<TerminalActionResponse> CloseAsync(string sessionId, string? reason = null, CancellationToken ct = default);
    Task<TerminalActionResponse> RenewGatewayAttachmentAsync(string sessionId, ulong generation, CancellationToken ct = default) =>
        Task.FromException<TerminalActionResponse>(new NotSupportedException("Gateway terminal browser attachment renewal is not available."));
    Task<TerminalActionResponse> RenewGatewayAttachmentAsync(
        string sessionId,
        ulong generation,
        string attachmentLeaseId,
        string browserAttachmentId,
        bool claimOwnership,
        CancellationToken ct = default) =>
        RenewGatewayAttachmentAsync(sessionId, generation, ct);
    Task<TerminalActionResponse> SendInputAsync(string sessionId, string data, CancellationToken ct = default);
    Task<ITerminalInputChannel> OpenInputChannelAsync(string sessionId, Action<TerminalInputChannelStatus>? onStatus = null, CancellationToken ct = default);
    Task<TerminalActionResponse> ResizeAsync(string sessionId, int cols, int rows, CancellationToken ct = default);
    IAsyncEnumerable<TerminalStreamMessage> StreamSessionAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<TerminalSessionDto>> GetSessionsAsync(string clientIdentityHex, CancellationToken ct = default);
    Task<TerminalSessionDto?> GetSessionAsync(string sessionId, CancellationToken ct = default);
    // Older test doubles and extension implementations predate the V2-only
    // restore probe. Do not silently fall back to the legacy route: callers
    // treat this explicit default failure as a recoverable gateway lookup.
    Task<TerminalSessionDto?> GetGatewaySessionAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromException<TerminalSessionDto?>(new NotSupportedException("Gateway terminal session lookup is not available."));
}

public interface ITerminalInputChannel : IAsyncDisposable
{
    ValueTask SendAsync(string data, CancellationToken ct = default);
    ValueTask SendImmediateAsync(string data, CancellationToken ct = default);
}

public sealed record TerminalInputChannelStatus(string State, string? Detail = null)
{
    public static TerminalInputChannelStatus NotStarted() => new("not-started");
    public static TerminalInputChannelStatus Opening() => new("opening");
    public static TerminalInputChannelStatus Connected() => new("connected");
    public static TerminalInputChannelStatus Reconnecting(string? detail = null) => new("reconnecting", detail);
    public static TerminalInputChannelStatus Fallback(string? detail = null) => new("rest-fallback", detail);
    // Sending telemetry is deliberately not a health signal. A send completion
    // can race an unsolicited socket close, so callers must only treat the
    // explicit Connected status as authority that a channel is usable.
    public static TerminalInputChannelStatus Sending(long frames, long bytes) => new("sending", $"frames={frames} bytes={bytes}");
    public static TerminalInputChannelStatus Failed(string detail) => new("failed", detail);
    public static TerminalInputChannelStatus Closed() => new("closed");
}

public sealed class TerminalService : ITerminalService
{
    private readonly IHttpClientFactory _factory;
    private readonly ITokenService _tokens;
    private readonly ILogger<TerminalService> _logger;
    // This is only a per-circuit optimization. A fresh circuit always probes V2
    // before it can decide that the opaque session identifier belongs to V1.
    private readonly ConcurrentDictionary<string, TerminalRoute> _sessionRoutes = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private enum TerminalRoute
    {
        Legacy,
        Gateway
    }

    public TerminalService(
        IHttpClientFactory factory,
        ITokenService tokens,
        ILogger<TerminalService> logger)
    {
        _factory = factory;
        _tokens = tokens;
        _logger = logger;
    }

    public Task EnsureSubscribedAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<TerminalOpenResponse> OpenSessionAsync(string clientIdentityHex, OpenTerminalRequest request, CancellationToken ct = default)
    {
        clientIdentityHex = NormalizeIdentityHex(clientIdentityHex);
        var http = _factory.CreateClient("OrchestratorApi");
        using var response = await http.PostAsJsonAsync($"/api/v1/clients/{clientIdentityHex}/terminal/open", request, ct).ConfigureAwait(false);
        return await ReadAsync<TerminalOpenResponse>(response, ct).ConfigureAwait(false);
    }

    public async Task<TerminalOpenResponse> OpenGatewaySessionAsync(int tenantId, Guid agentId, OpenTerminalRequest request, CancellationToken ct = default)
    {
        var http = _factory.CreateClient("OrchestratorApi");
        using var response = await http.PostAsJsonAsync($"/api/v2/agents/{tenantId}/{agentId:D}/terminal/sessions", request, ct).ConfigureAwait(false);
        var opened = await ReadAsync<TerminalOpenResponse>(response, ct).ConfigureAwait(false);
        _sessionRoutes[opened.SessionId] = TerminalRoute.Gateway;
        return opened;
    }

    public async Task<TerminalActionResponse> CloseAsync(string sessionId, string? reason = null, CancellationToken ct = default)
    {
        var http = _factory.CreateClient("OrchestratorApi");
        var route = await ResolveRouteAsync(sessionId, ct).ConfigureAwait(false);
        using var response = await http.PostAsJsonAsync(
            route == TerminalRoute.Gateway ? $"/api/v2/gateway-terminal/{sessionId}/close" : $"/api/v1/terminal/{sessionId}/close",
            new CloseTerminalRequest(reason),
            ct).ConfigureAwait(false);
        return await ReadAsync<TerminalActionResponse>(response, ct).ConfigureAwait(false);
    }

    public async Task<TerminalActionResponse> RenewGatewayAttachmentAsync(
        string sessionId,
        ulong generation,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfZero(generation);

        using var http = _factory.CreateClient("OrchestratorApi");
        using var response = await http.PostAsJsonAsync(
            $"/api/v2/gateway-terminal/{sessionId}/attachment/renew",
            new TerminalAttachmentRenewalRequest(generation),
            ct).ConfigureAwait(false);
        _sessionRoutes[sessionId] = TerminalRoute.Gateway;
        return await ReadAsync<TerminalActionResponse>(response, ct).ConfigureAwait(false);
    }

    public async Task<TerminalActionResponse> RenewGatewayAttachmentAsync(
        string sessionId,
        ulong generation,
        string attachmentLeaseId,
        string browserAttachmentId,
        bool claimOwnership,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfZero(generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentLeaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(browserAttachmentId);

        using var http = _factory.CreateClient("OrchestratorApi");
        using var response = await http.PostAsJsonAsync(
            $"/api/v2/gateway-terminal/{sessionId}/attachment/renew",
            new TerminalAttachmentRenewalRequest(generation)
            {
                AttachmentLeaseId = attachmentLeaseId,
                BrowserAttachmentId = browserAttachmentId,
                ClaimOwnership = claimOwnership
            },
            ct).ConfigureAwait(false);
        _sessionRoutes[sessionId] = TerminalRoute.Gateway;
        return await ReadAsync<TerminalActionResponse>(response, ct).ConfigureAwait(false);
    }

    public async Task<ITerminalInputChannel> OpenInputChannelAsync(
        string sessionId,
        Action<TerminalInputChannelStatus>? onStatus = null,
        CancellationToken ct = default)
    {
        var http = _factory.CreateClient("OrchestratorApi");
        var route = await ResolveRouteAsync(sessionId, ct).ConfigureAwait(false);
        var token = await _tokens.GetValidAccessTokenAsync().ConfigureAwait(false);
        var channel = new WebSocketTerminalInputChannel(
            sessionId,
            BuildWebSocketUri(http.BaseAddress, route == TerminalRoute.Gateway ? $"/api/v2/gateway-terminal/{sessionId}/stdin/ws" : $"/api/v1/terminal/{sessionId}/stdin/ws"),
            token,
            SendInputAsync,
            onStatus,
            _logger);
        try
        {
            await channel.ConnectAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return channel;
        }
        catch
        {
            // A connect operation owns native websocket and cancellation resources
            // even when it never returns a channel to the caller. Dispose them on
            // every exceptional/cancelled path so an abandoned attempt cannot keep
            // a close monitor or input pump alive.
            try
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeException)
            {
                _logger.LogDebug(disposeException, "[Terminal] Input websocket cleanup after failed open did not complete session={SessionId}", sessionId);
            }

            throw;
        }
    }

    public async Task<TerminalActionResponse> SendInputAsync(string sessionId, string data, CancellationToken ct = default)
    {
        using var http = _factory.CreateClient("OrchestratorApi");
        var route = await ResolveRouteAsync(sessionId, ct).ConfigureAwait(false);
        using var response = await http.PostAsJsonAsync(
            route == TerminalRoute.Gateway ? $"/api/v2/gateway-terminal/{sessionId}/stdin" : $"/api/v1/terminal/{sessionId}/stdin",
            new TerminalInputRequest(data),
            ct).ConfigureAwait(false);
        return await ReadAsync<TerminalActionResponse>(response, ct).ConfigureAwait(false);
    }

    public async Task<TerminalActionResponse> ResizeAsync(string sessionId, int cols, int rows, CancellationToken ct = default)
    {
        using var http = _factory.CreateClient("OrchestratorApi");
        var route = await ResolveRouteAsync(sessionId, ct).ConfigureAwait(false);
        using var response = await http.PostAsJsonAsync(
            route == TerminalRoute.Gateway ? $"/api/v2/gateway-terminal/{sessionId}/resize" : $"/api/v1/terminal/{sessionId}/resize",
            new TerminalResizeRequest(cols, rows),
            ct).ConfigureAwait(false);
        return await ReadAsync<TerminalActionResponse>(response, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TerminalStreamMessage> StreamSessionAsync(string sessionId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var http = _factory.CreateClient("OrchestratorApiStreaming");
        var route = await ResolveRouteAsync(sessionId, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            route == TerminalRoute.Gateway ? $"/api/v2/gateway-terminal/{sessionId}/stream" : $"/api/v1/terminal/{sessionId}/stream");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192);

        string? line;
        var data = new StringBuilder();
        var eventType = "message";
        var eventCount = 0;
        while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    eventCount++;
                    var payloadText = data.ToString();
                    if (eventCount <= 10)
                    {
                        _logger.LogInformation(
                            "[Terminal] SSE event received session={SessionId} event={EventType} index={Index} payloadChars={PayloadChars} payloadBytes={PayloadBytes}",
                            sessionId,
                            eventType,
                            eventCount,
                            payloadText.Length,
                            Encoding.UTF8.GetByteCount(payloadText));
                    }

                    TerminalStreamMessage? payload = null;
                    TerminalStreamMessage? parseError = null;
                    try
                    {
                        payload = JsonSerializer.Deserialize<TerminalStreamMessage>(payloadText, JsonOptions);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(
                            ex,
                            "[Terminal] SSE parse failed session={SessionId} event={EventType} index={Index} payloadChars={PayloadChars}",
                            sessionId,
                            eventType,
                            eventCount,
                            payloadText.Length);
                        parseError = new TerminalStreamMessage(
                            Kind: "error",
                            Reason: $"Malformed terminal SSE payload ({eventType}, event {eventCount}).");
                    }

                    if (parseError is not null)
                    {
                        yield return parseError;
                    }

                    if (payload is not null)
                    {
                        if (string.Equals(payload.Kind, "data", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation(
                                "[Terminal] SSE output payload parsed session={SessionId} direction={Direction} seq={Sequence} chars={Chars} bytes={Bytes}",
                                sessionId,
                                payload.Direction ?? string.Empty,
                                payload.Sequence,
                                payload.Data?.Length ?? 0,
                                string.IsNullOrEmpty(payload.Data) ? 0 : Encoding.UTF8.GetByteCount(payload.Data));
                        }

                        yield return payload;
                    }
                }

                data.Clear();
                eventType = "message";
                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventType = line.Length > 6 ? line[6..].Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(eventType))
                {
                    eventType = "message";
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line.Length > 5 ? line[5..].TrimStart() : string.Empty;
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(value);
            }
            else
            {
                _logger.LogWarning(
                    "[Terminal] Ignoring unsupported SSE line session={SessionId} event={EventType} chars={Chars}",
                    sessionId,
                    eventType,
                    line.Length);
            }
        }
    }

    public async Task<IReadOnlyList<TerminalSessionDto>> GetSessionsAsync(string clientIdentityHex, CancellationToken ct = default)
    {
        clientIdentityHex = NormalizeIdentityHex(clientIdentityHex);
        using var http = _factory.CreateClient("OrchestratorApi");
        var result = await http.GetFromJsonAsync<IReadOnlyList<TerminalSessionDto>>(
            $"/api/v1/clients/{clientIdentityHex}/terminal/sessions",
            cancellationToken: ct).ConfigureAwait(false);

        return result ?? Array.Empty<TerminalSessionDto>();
    }

    public async Task<TerminalSessionDto?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var http = _factory.CreateClient("OrchestratorApi");
        if (_sessionRoutes.TryGetValue(sessionId, out var knownRoute))
        {
            using var knownResponse = await http.GetAsync(
                knownRoute == TerminalRoute.Gateway ? $"/api/v2/gateway-terminal/{sessionId}" : $"/api/v1/terminal/{sessionId}",
                ct).ConfigureAwait(false);
            if (knownResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            return await ReadAsync<TerminalSessionDto>(knownResponse, ct).ConfigureAwait(false);
        }

        using var gatewayResponse = await http.GetAsync($"/api/v2/gateway-terminal/{sessionId}", ct).ConfigureAwait(false);
        if (gatewayResponse.IsSuccessStatusCode)
        {
            _sessionRoutes[sessionId] = TerminalRoute.Gateway;
            return await ReadAsync<TerminalSessionDto>(gatewayResponse, ct).ConfigureAwait(false);
        }

        if (gatewayResponse.StatusCode != HttpStatusCode.NotFound)
        {
            return await ReadAsync<TerminalSessionDto>(gatewayResponse, ct).ConfigureAwait(false);
        }

        using var legacyResponse = await http.GetAsync($"/api/v1/terminal/{sessionId}", ct).ConfigureAwait(false);
        if (legacyResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        _sessionRoutes[sessionId] = TerminalRoute.Legacy;
        return await ReadAsync<TerminalSessionDto>(legacyResponse, ct).ConfigureAwait(false);
    }

    public async Task<TerminalSessionDto?> GetGatewaySessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var http = _factory.CreateClient("OrchestratorApi");
        using var response = await http.GetAsync($"/api/v2/gateway-terminal/{sessionId}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _sessionRoutes.TryRemove(sessionId, out _);
            return null;
        }

        _sessionRoutes[sessionId] = TerminalRoute.Gateway;
        return await ReadAsync<TerminalSessionDto>(response, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var content = response.Content;
        var hasBody = content.Headers.ContentLength.GetValueOrDefault() > 0;

        if (hasBody)
        {
            var payload = await content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && payload is not null)
            {
                return payload;
            }
        }

        if (response.IsSuccessStatusCode && typeof(T) == typeof(TerminalOpenResponse))
        {
            var location = response.Headers.Location?.ToString();
            var sessionId = ExtractSessionId(location);
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                return (T)(object)new TerminalOpenResponse(
                    TrackingId: Guid.NewGuid().ToString("n"),
                    SessionId: sessionId,
                    Message: "Terminal opening.");
            }
        }

        var message = hasBody
            ? await content.ReadAsStringAsync(ct).ConfigureAwait(false)
            : $"Empty response body (status {(int)response.StatusCode}).";
        throw new HttpRequestException(
            $"Terminal API call failed with status {(int)response.StatusCode}: {message}",
            null,
            response.StatusCode);
    }

    private static string? ExtractSessionId(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var trimmed = location.TrimEnd('/');
        var sessionId = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
    }

    private static string NormalizeIdentityHex(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

    private async Task<TerminalRoute> ResolveRouteAsync(string sessionId, CancellationToken ct)
    {
        if (_sessionRoutes.TryGetValue(sessionId, out var knownRoute))
        {
            return knownRoute;
        }

        using var http = _factory.CreateClient("OrchestratorApi");
        using var response = await http.GetAsync($"/api/v2/gateway-terminal/{sessionId}", ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            _sessionRoutes[sessionId] = TerminalRoute.Gateway;
            return TerminalRoute.Gateway;
        }

        // A V2 route that reports anything except absence is still authoritative.
        // In particular, a reconnecting transport must not be silently redirected
        // into the unrelated V1 terminal API.
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            return TerminalRoute.Gateway;
        }

        _sessionRoutes[sessionId] = TerminalRoute.Legacy;
        return TerminalRoute.Legacy;
    }

    private static Uri BuildWebSocketUri(Uri? baseAddress, string relativePath)
    {
        if (baseAddress is null)
        {
            throw new InvalidOperationException("The OrchestratorApi base address is not configured.");
        }

        var uri = new Uri(baseAddress, relativePath);
        var builder = new UriBuilder(uri)
        {
            Scheme = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws"
        };
        return builder.Uri;
    }

    private sealed class WebSocketTerminalInputChannel : ITerminalInputChannel
    {
        private const int MaxBatchChars = 256;
        private static readonly TimeSpan MaxBatchDelay = TimeSpan.FromMilliseconds(4);

        private readonly string _sessionId;
        private readonly Uri _uri;
        private readonly string _token;
        private readonly Func<string, string, CancellationToken, Task<TerminalActionResponse>> _fallback;
        private readonly Action<TerminalInputChannelStatus>? _onStatus;
        private readonly ILogger _logger;
        private readonly Channel<string> _queue;
        private readonly CancellationTokenSource _cts = new();
        private readonly ClientWebSocket _socket = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private Task? _pumpTask;
        private Task? _closeMonitorTask;
        private volatile bool _useFallback;
        private bool _wasConnected;
        private int _terminalTransportReconnecting;
        private int _disposed;
        private long _sentFrames;
        private long _sentBytes;

        public WebSocketTerminalInputChannel(
            string sessionId,
            Uri uri,
            string token,
            Func<string, string, CancellationToken, Task<TerminalActionResponse>> fallback,
            Action<TerminalInputChannelStatus>? onStatus,
            ILogger logger)
        {
            _sessionId = sessionId;
            _uri = uri;
            _token = token;
            _fallback = fallback;
            _onStatus = onStatus;
            _logger = logger;
            _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        }

        public async Task ConnectAsync(CancellationToken ct)
        {
            Notify(TerminalInputChannelStatus.Opening());
            _logger.LogInformation("[Terminal] Input websocket opening session={SessionId} uri={Uri}", _sessionId, _uri);
            try
            {
                _socket.Options.SetRequestHeader("Authorization", $"Bearer {_token}");
                await _socket.ConnectAsync(_uri, ct).ConfigureAwait(false);
                _wasConnected = true;
                _logger.LogInformation("[Terminal] Input websocket connected session={SessionId} state={State}", _sessionId, _socket.State);
                Notify(TerminalInputChannelStatus.Connected());
                _closeMonitorTask = Task.Run(() => MonitorCloseAsync(_cts.Token));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableTerminalTransportLoss(ex))
            {
                EnterReconnecting();
            }
            catch (WebSocketException ex) when (!IsWebSocketUpgradeUnavailable(ex))
            {
                EnterReconnecting();
            }
            catch (Exception ex)
            {
                _useFallback = true;
                _logger.LogWarning(ex, "[Terminal] Input websocket open failed; falling back to REST stdin session={SessionId}", _sessionId);
                Notify(TerminalInputChannelStatus.Fallback("WebSocket input is unavailable."));
            }

            _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
        }

        private async Task MonitorCloseAsync(CancellationToken ct)
        {
            var buffer = new byte[256];

            try
            {
                while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType != WebSocketMessageType.Close)
                    {
                        continue;
                    }

                    // A peer-initiated normal close is still an unexpected loss
                    // of this browser input transport. Only our DisposeAsync
                    // cancellation is intentional. Treat every other close as a
                    // reconnect so the owning console drops this closed channel
                    // and retries after lifecycle polling reports the session open.
                    EnterReconnecting();

                    return;
                }

                if (!ct.IsCancellationRequested)
                {
                    EnterReconnecting();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (IsRecoverableTerminalTransportLoss(ex))
            {
                EnterReconnecting();
            }
            catch (WebSocketException)
            {
                EnterReconnecting();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Terminal] Input websocket close monitor failed session={SessionId}", _sessionId);
                Notify(TerminalInputChannelStatus.Failed("Input websocket monitoring failed."));
            }
        }

        public ValueTask SendAsync(string data, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(data))
            {
                return ValueTask.CompletedTask;
            }

            // Input entered after transport loss is intentionally not replayed.
            // Avoid accumulating it in the bounded queue while the dialog waits
            // for the server to report the session active again.
            if (IsReconnecting)
            {
                return ValueTask.CompletedTask;
            }

            return _queue.Writer.WriteAsync(data, ct);
        }

        public async ValueTask SendImmediateAsync(string data, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            var control = DescribeControlInput(data);
            if (!string.IsNullOrWhiteSpace(control))
            {
                _logger.LogInformation(
                    "[Terminal] browser.stdin.{Control} session={SessionId} bytes={Bytes}",
                    control,
                    _sessionId,
                    Encoding.UTF8.GetByteCount(data));
            }

            await SendBatchAsync(data, ct, immediate: true).ConfigureAwait(false);
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            var batch = new StringBuilder(MaxBatchChars);

            try
            {
                while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    batch.Clear();

                    while (batch.Length < MaxBatchChars && _queue.Reader.TryRead(out var first))
                    {
                        batch.Append(first);
                        if (ShouldFlushImmediately(first))
                        {
                            break;
                        }
                    }

                    if (batch.Length == 0)
                    {
                        continue;
                    }

                    var value = batch.ToString();
                    if (!ShouldFlushImmediately(value) && value.Length < MaxBatchChars)
                    {
                        await Task.Delay(MaxBatchDelay, ct).ConfigureAwait(false);

                        while (batch.Length < MaxBatchChars && _queue.Reader.TryRead(out var next))
                        {
                            batch.Append(next);
                            if (ShouldFlushImmediately(next))
                            {
                                break;
                            }
                        }

                        value = batch.ToString();
                    }

                    await SendBatchAsync(value, ct, immediate: false).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Terminal] Input channel pump failed session={SessionId}", _sessionId);
                Notify(TerminalInputChannelStatus.Failed(ex.Message));
            }
        }

        private async Task SendBatchAsync(string data, CancellationToken ct, bool immediate)
        {
            if (IsReconnecting)
            {
                return;
            }

            if (_useFallback)
            {
                await _fallback(_sessionId, data, ct).ConfigureAwait(false);
                RecordSent(data, immediate);
                return;
            }

            if (_socket.State != WebSocketState.Open)
            {
                if (_wasConnected || IsRecoverableTerminalTransportClose(_socket.CloseStatus, _socket.CloseStatusDescription))
                {
                    EnterReconnecting();
                    return;
                }

                _useFallback = true;
                _logger.LogInformation("[Terminal] Input websocket is unavailable; using REST stdin session={SessionId}", _sessionId);
                Notify(TerminalInputChannelStatus.Fallback("WebSocket input is unavailable."));
                await _fallback(_sessionId, data, ct).ConfigureAwait(false);
                RecordSent(data, immediate);
                return;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(data);
                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _socket.SendAsync(
                        bytes,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        cancellationToken: ct).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }

                RecordSent(data, immediate);
            }
            catch (Exception ex) when (IsRecoverableTerminalTransportLoss(ex) ||
                                       IsRecoverableTerminalTransportClose(_socket.CloseStatus, _socket.CloseStatusDescription))
            {
                EnterReconnecting();
            }
            catch (WebSocketException)
            {
                EnterReconnecting();
            }
            catch (InvalidOperationException ex)
            {
                if (_socket.State != WebSocketState.Open)
                {
                    EnterReconnecting();
                    return;
                }

                _useFallback = true;
                _logger.LogWarning(ex, "[Terminal] Input websocket send failed; falling back to REST stdin session={SessionId}", _sessionId);
                Notify(TerminalInputChannelStatus.Fallback("WebSocket input is unavailable."));
                await _fallback(_sessionId, data, ct).ConfigureAwait(false);
                RecordSent(data, immediate);
            }
        }

        private bool IsReconnecting => Volatile.Read(ref _terminalTransportReconnecting) != 0;

        private void EnterReconnecting()
        {
            if (Interlocked.Exchange(ref _terminalTransportReconnecting, 1) != 0)
            {
                return;
            }

            _logger.LogInformation("[Terminal] Input websocket awaiting terminal transport recovery session={SessionId}", _sessionId);
            Notify(TerminalInputChannelStatus.Reconnecting("Terminal transport is reconnecting."));
        }

        private static bool IsRecoverableTerminalTransportClose(WebSocketCloseStatus? closeStatus, string? description) =>
            closeStatus == WebSocketCloseStatus.EndpointUnavailable ||
            HasRecoverableTerminalTransportCode(description);

        private static bool IsRecoverableTerminalTransportLoss(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is HttpRequestException
                    {
                        StatusCode: HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                    })
                {
                    return true;
                }

                if (HasRecoverableTerminalTransportCode(current.Message))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRecoverableTerminalTransportCode(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            (value.Contains("terminal_transport_reconnecting", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("terminal_transport_unavailable", StringComparison.OrdinalIgnoreCase) ||
             (value.Contains("status code", StringComparison.OrdinalIgnoreCase) &&
              (value.Contains("409", StringComparison.Ordinal) || value.Contains("503", StringComparison.Ordinal))));

        private static bool IsWebSocketUpgradeUnavailable(WebSocketException exception) =>
            exception.Message.Contains("status code", StringComparison.OrdinalIgnoreCase) &&
            (exception.Message.Contains("400", StringComparison.Ordinal) ||
             exception.Message.Contains("404", StringComparison.Ordinal) ||
             exception.Message.Contains("405", StringComparison.Ordinal) ||
             exception.Message.Contains("426", StringComparison.Ordinal));

        private void RecordSent(string data, bool immediate)
        {
            var frames = Interlocked.Increment(ref _sentFrames);
            var lastBytes = Encoding.UTF8.GetByteCount(data);
            var bytes = Interlocked.Add(ref _sentBytes, lastBytes);
            var control = DescribeControlInput(data);
            if (immediate || frames <= 3 || frames % 50 == 0)
            {
                _logger.LogInformation(
                    "[Terminal] Browser stdin dispatched session={SessionId} frames={Frames} bytes={Bytes} lastBytes={LastBytes} mode={Mode} immediate={Immediate} control={Control}",
                    _sessionId,
                    frames,
                    bytes,
                    lastBytes,
                    _useFallback ? "rest" : "websocket",
                    immediate,
                    control ?? string.Empty);
                Notify(TerminalInputChannelStatus.Sending(frames, bytes));
            }
        }

        private void Notify(TerminalInputChannelStatus status)
        {
            if (IsReconnecting &&
                (string.Equals(status.State, "connected", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status.State, "sending", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            try
            {
                _onStatus?.Invoke(status);
            }
            catch
            {
            }
        }

        private static bool ShouldFlushImmediately(string value) =>
            value.IndexOfAny(['\r', '\n', '\u0003', '\u001b']) >= 0;

        private static string? DescribeControlInput(string value)
        {
            if (value.Contains('\u0003', StringComparison.Ordinal))
            {
                return "ctrl_c";
            }

            if (value.Contains('\u0004', StringComparison.Ordinal))
            {
                return "ctrl_d";
            }

            if (value.Contains('\u001a', StringComparison.Ordinal))
            {
                return "ctrl_z";
            }

            return null;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _queue.Writer.TryComplete();
            try { _cts.Cancel(); } catch { }

            if (_pumpTask is not null)
            {
                try { await _pumpTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            }

            if (_closeMonitorTask is not null)
            {
                try { await _closeMonitorTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            }

            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "terminal-input-dispose", closeCts.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _socket.Dispose();
            _sendLock.Dispose();
            _cts.Dispose();
            Notify(TerminalInputChannelStatus.Closed());
        }
    }

}

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NetRatel.Shared.Operations;

namespace NetRatel.Mcp;

/// <summary>
/// Bounded, transparent stdio/Streamable HTTP relay in the versioned MCP distribution.
/// HTTP uses the maintained .NET transport. Raw envelopes avoid catalog translation and
/// preserve request IDs; bounded framing also covers peers that do not bound SSE events.
/// No request is automatically replayed, including after session expiry or an uncertain result.
/// </summary>
public sealed class HttpMcpBridge(HttpClient http, Uri endpoint, Func<CancellationToken, Task<string>> getToken,
    TimeSpan? requestTimeout = null)
{
    public const int MaximumOutstandingRequests = 8;
    public const int MaximumFrameCharacters = 1_048_576;
    public const int MaximumResponseCharacters = 16 * MaximumFrameCharacters;
    private readonly TimeSpan _timeout = requestTimeout ?? TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _outputGate = new(1);
    private readonly SemaphoreSlim _requests = new(MaximumOutstandingRequests);
    private readonly SemaphoreSlim _controls = new(MaximumOutstandingRequests);
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private volatile string? _sessionId;
    private volatile string _protocolVersion = "2025-03-26";
    private Task? _events;
    private volatile bool _initialized;
    private readonly object _eventsGate = new();

    public async Task RunAsync(TextReader input, TextWriter output, TextWriter diagnostics, CancellationToken ct = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var active = new HashSet<Task>();
        Task? failed = null;
        var inputFrames = new BoundedLineReader(input, drainOverflow: true);
        try
        {
            while (await inputFrames.ReadLineAsync(lifetime.Token) is { } line)
            {
                JsonElement message;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    message = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    await ErrorAsync(output, null, -32700, "Parse error", "parse_error", ct: ct);
                    continue;
                }
                if (!IsEnvelope(message))
                {
                    await ErrorAsync(output, null, -32600, "Invalid Request", "invalid_request", ct: ct);
                    continue;
                }
                var hasId = message.TryGetProperty("id", out var id);
                var hasMethod = message.TryGetProperty("method", out var method);
                var request = hasId && hasMethod;
                if (hasId && IdKey(id) is null || hasMethod && method.ValueKind != JsonValueKind.String ||
                    !hasMethod && (!hasId || message.TryGetProperty("result", out _) == message.TryGetProperty("error", out _)))
                {
                    await ErrorAsync(output, null, -32600, "Invalid Request", "invalid_request", ct: ct);
                    continue;
                }
                var cancellation = !hasId && hasMethod && method.GetString() == "notifications/cancelled";
                var slots = cancellation || !hasMethod ? _controls : _requests;
                if (!slots.Wait(0))
                {
                    if (request) await ErrorAsync(output, id, -32001, "Outstanding request limit reached", "client_busy", ct: ct);
                    else await diagnostics.WriteLineAsync("HTTP MCP notification capacity reached; reconnect and reconcile if necessary.");
                    continue;
                }
                Pending? pending = null;
                if (request)
                {
                    pending = new(id, lifetime.Token);
                    if (!_pending.TryAdd(IdKey(id)!, pending))
                    {
                        pending.Dispose(); slots.Release();
                        await ErrorAsync(output, id, -32600, "Request ID is already outstanding", "duplicate_request_id", ct: ct);
                        continue;
                    }
                }
                var task = ForwardAsync(message, pending, cancellation, slots, output, diagnostics, lifetime.Token);
                lock (active) active.Add(task);
                _ = task.ContinueWith(completed =>
                {
                    lock (active)
                    {
                        active.Remove(completed);
                        if (completed.IsFaulted) failed ??= completed;
                    }
                    if (completed.IsFaulted) lifetime.Cancel();
                },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            Task[] remaining;
            lock (active) remaining = active.ToArray();
            await Task.WhenAll(remaining);
            if (failed is not null) await failed;
        }
        catch (OperationCanceledException) when (failed is not null)
        { await failed; throw; }
        finally
        {
            lifetime.Cancel();
            Task[] cleanup;
            lock (active) cleanup = active.ToArray();
            try { await Task.WhenAll(cleanup).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (TimeoutException)
            { await diagnostics.WriteLineAsync("HTTP MCP shutdown did not finish within its budget; reconcile outstanding operations before reconnecting."); }
            if (_events is not null)
            {
                try { await _events.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (TimeoutException)
                { await diagnostics.WriteLineAsync("HTTP MCP event stream shutdown exceeded its budget; reconnect only after reconciling state."); }
            }
        }
    }

    private async Task ForwardAsync(JsonElement message, Pending? pending, bool cancellation, SemaphoreSlim slots,
        TextWriter output, TextWriter diagnostics, CancellationToken lifetime)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(pending?.Cancellation.Token ?? lifetime);
        timeout.CancelAfter(_timeout);
        var replied = false;
        try
        {
            var initializing = message.TryGetProperty("method", out var requestMethod) && requestMethod.GetString() == "initialize";
            using var request = await CreateRequestAsync(HttpMethod.Post, timeout.Token, includeProtocolVersion: !initializing);
            request.Content = new StringContent(message.GetRawText(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                if (pending is not null)
                {
                    var retry = response.Headers.RetryAfter;
                    var admission = await ReadAdmissionFailureAsync(response, timeout.Token);
                    await ErrorAsync(output, pending.Id, -32002, "HTTP MCP request was rejected", admission?.Failure.Code ??
                        (response.StatusCode == HttpStatusCode.TooManyRequests ? "rate_limited" :
                            response.StatusCode == HttpStatusCode.NotFound ? "session_or_endpoint_missing" : "http_error"),
                        (int)response.StatusCode, SafeRetryDelay(retry), SafeRetryDate(retry), lifetime,
                        admission?.Failure, admission?.CorrelationId);
                    replied = true;
                }
                else await diagnostics.WriteLineAsync("HTTP MCP notification was rejected; inspect the selected connection before continuing.");
                return;
            }
            if (message.TryGetProperty("method", out var method) && method.GetString() == "initialize" &&
                response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                var sessions = values.Take(2).ToArray();
                if (sessions.Length != 1) throw new JsonException();
                var session = sessions[0];
                if (session.Length > 4096 || session.Any(character => character < '!' || character > '~'))
                    throw new JsonException();
                _sessionId = session;
            }
            if (cancellation && message.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("requestId", out var cancelledId) && IdKey(cancelledId) is { } key &&
                _pending.TryGetValue(key, out var cancelled))
                cancelled.Cancel();
            if (pending is null)
            {
                // Session GET follows the accepted initialized notification, never initialize itself.
                if (message.TryGetProperty("method", out var notified) && notified.GetString() == "notifications/initialized" &&
                    _initialized && _sessionId is not null)
                {
                    lock (_eventsGate) _events ??= ReceiveEventsAsync(output, diagnostics, lifetime);
                }
                return; // Notifications never receive manufactured request replies.
            }
            await ConsumeAsync(response, async frame =>
            {
                if (!IsEnvelope(frame)) throw new JsonException();
                if (frame.TryGetProperty("id", out var responseId) && !frame.TryGetProperty("method", out _))
                {
                    if (IdKey(responseId) != IdKey(pending.Id) || replied ||
                        frame.TryGetProperty("result", out _) == frame.TryGetProperty("error", out _)) throw new JsonException();
                    replied = true;
                    if (message.GetProperty("method").GetString() == "initialize" && frame.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
                        result.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String &&
                        version.GetString() is "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25")
                    {
                        _protocolVersion = version.GetString()!;
                        _initialized = true;
                    }
                }
                else if (!frame.TryGetProperty("method", out var receivedMethod) || receivedMethod.ValueKind != JsonValueKind.String)
                    throw new JsonException();
                await WriteAsync(output, frame, lifetime);
                return !replied; // A final JSON-RPC response completes the request even if HTTP SSE remains open.
            }, timeout.Token);
            if (!replied) throw new JsonException();
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            if (pending is not null && !replied)
                await ErrorAsync(output, pending.Id, pending.Cancellation.IsCancellationRequested ? -32800 : -32003,
                    pending.Cancellation.IsCancellationRequested ? "Request cancelled" : "HTTP MCP request timed out",
                    pending.Cancellation.IsCancellationRequested ? "request_cancelled" : "transport_timeout", ct: lifetime);
            else if (pending is null) await diagnostics.WriteLineAsync("HTTP MCP notification timed out; its outcome is uncertain.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or DecoderFallbackException or
            NetRatel.AgentClient.AgentClientRemoteException or NetRatel.AgentClient.AgentClientValidationException or FormatException)
        {
            if (pending is not null && !replied)
                await ErrorAsync(output, pending.Id, -32004, "HTTP MCP response was unavailable or invalid", "transport_response_invalid", ct: lifetime);
            else await diagnostics.WriteLineAsync("HTTP MCP response failed; inspect the selected connection and reconcile before retrying.");
        }
        finally
        {
            if (pending is not null) { _pending.TryRemove(IdKey(pending.Id)!, out _); pending.Dispose(); }
            slots.Release();
        }
    }

    private async Task ReceiveEventsAsync(TextWriter output, TextWriter diagnostics, CancellationToken ct)
    {
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Get, ct);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed) return;
            response.EnsureSuccessStatusCode();
            await ConsumeAsync(response, async frame =>
            {
                if (!IsEnvelope(frame) || !frame.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                    throw new JsonException();
                await WriteAsync(output, frame, ct);
                return true;
            }, ct);
            await diagnostics.WriteLineAsync("HTTP MCP event stream ended; reconnect explicitly before relying on unsolicited notifications.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or DecoderFallbackException or
            NetRatel.AgentClient.AgentClientRemoteException or NetRatel.AgentClient.AgentClientValidationException or FormatException)
        { await diagnostics.WriteLineAsync("HTTP MCP event stream ended; reconnect explicitly before relying on unsolicited notifications."); }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, CancellationToken ct, bool includeProtocolVersion = true)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await getToken(ct));
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        // The initialize body owns version negotiation. Sending the previous/default
        // version here can contradict that body and is rejected by the HTTP server.
        if (includeProtocolVersion) request.Headers.Add("MCP-Protocol-Version", _protocolVersion);
        if (_sessionId is { } session) request.Headers.Add("Mcp-Session-Id", session);
        return request;
    }

    private static async Task ConsumeAsync(HttpResponseMessage response, Func<JsonElement, Task<bool>> emit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        var frames = new BoundedLineReader(reader);
        var total = 0;
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var json = new StringBuilder();
            while (await frames.ReadLineAsync(ct) is { } line)
            {
                if (json.Length + line.Length + 1 > MaximumFrameCharacters) throw new JsonException();
                json.Append(line).Append('\n');
            }
            using var document = JsonDocument.Parse(json.ToString());
            await emit(document.RootElement);
            return;
        }
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new JsonException();
        var data = new StringBuilder();
        var eventType = "message";
        while (await frames.ReadLineAsync(ct) is { } line)
        {
            total += line.Length + 1;
            if (total > MaximumResponseCharacters) throw new JsonException();
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    if (eventType == "message")
                    {
                        using var document = JsonDocument.Parse(data.ToString());
                        if (!await emit(document.RootElement)) return;
                    }
                    data.Clear();
                }
                eventType = "message";
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line[6..].TrimStart(' ');
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var value = line.AsMemory(5);
            if (value.Span.StartsWith(" ")) value = value[1..];
            if (data.Length + value.Length + 1 > MaximumFrameCharacters) throw new JsonException();
            data.Append(value.Span).Append('\n');
        }
        if (data.Length > 0) throw new JsonException(); // Truncated SSE events are never treated as completed replies.
    }

    private sealed class BoundedLineReader(TextReader reader, bool drainOverflow = false)
    {
        private bool _skipLf;
        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var line = new StringBuilder();
            var character = new char[1];
            var overflow = false;
            while (await ReadCharacterAsync(character, ct) != 0)
            {
                if (_skipLf)
                {
                    _skipLf = false;
                    if (character[0] == '\n') continue;
                }
                if (character[0] is '\r' or '\n')
                {
                    _skipLf = character[0] == '\r';
                    return overflow ? "{" : line.ToString();
                }
                if (line.Length >= MaximumFrameCharacters)
                {
                    if (!drainOverflow) throw new JsonException();
                    overflow = true; continue;
                }
                line.Append(character[0]);
            }
            return overflow ? "{" : line.Length == 0 ? null : line.ToString();
        }
        private async ValueTask<int> ReadCharacterAsync(Memory<char> character, CancellationToken ct)
        {
            // Console stdin can ignore cancellation. Bound our wait even when the OS read cannot be interrupted.
            var read = reader.ReadAsync(character, ct);
            return read.IsCompletedSuccessfully ? read.Result : await read.AsTask().WaitAsync(ct);
        }
    }
    private sealed record AdmissionFailure(McpDelegationFailure Failure, string? CorrelationId);
    private static async Task<AdmissionFailure?> ReadAdmissionFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int maximumBytes = 16 * 1024;
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) ||
            response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength is > maximumBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        try
        {
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var supplied) || supplied.ValueKind != JsonValueKind.String)
                return null;
            var code = supplied.GetString();
            if (code is not ("local_credential_invalid" or "local_credential_expired" or "local_credential_revoked" or
                "delegated_identity_revoked" or "local_credential_forbidden" or "local_credential_permission_denied")) return null;
            // Remediation is reconstructed from the shared closed vocabulary, never taken from remote text.
            if (McpDelegationFailure.ForCode(code) is not { } failure) return null;
            var correlation = root.TryGetProperty("correlationId", out var field) && field.ValueKind == JsonValueKind.String &&
                field.GetString() is { Length: > 0 and <= 128 } value &&
                value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':') ? value : null;
            return new(failure, correlation);
        }
        catch (JsonException) { return null; }
    }

    private static double? SafeRetryDelay(RetryConditionHeaderValue? retry)
        => retry?.Delta is { TotalSeconds: >= 0 and <= 604800 } delay ? delay.TotalSeconds : null;
    private static DateTimeOffset? SafeRetryDate(RetryConditionHeaderValue? retry)
        => retry?.Date is { } date && date >= DateTimeOffset.UtcNow && date <= DateTimeOffset.UtcNow.AddDays(7) ? date : null;
    private static bool IsEnvelope(JsonElement message) => message.ValueKind == JsonValueKind.Object &&
        message.TryGetProperty("jsonrpc", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == "2.0";
    private static string? IdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String when id.GetString() is { Length: <= 4096 } text => "s:" + text,
        JsonValueKind.Number when id.TryGetInt64(out var number) => "n:" + number.ToString(CultureInfo.InvariantCulture),
        _ => null
    };
    private async Task ErrorAsync(TextWriter output, JsonElement? id, int code, string message, string reason,
        int? httpStatus = null, double? retryAfterSeconds = null, DateTimeOffset? retryAfterUtc = null, CancellationToken ct = default,
        McpDelegationFailure? failure = null, string? correlationId = null)
    {
        var local = reason is "parse_error" or "invalid_request" or "client_busy" or "duplicate_request_id";
        await WriteAsync(output, JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id, error = new { code, message,
            data = new { code = reason, httpStatus, retryAfterSeconds, retryAfterUtc,
                failure = failure is null ? null : new { code = failure.Code, layer = failure.Layer, retryable = failure.Retryable, remediation = failure.Remediation }, correlationId,
                outcome = local ? "not_dispatched" : failure is not null ? "not_admitted" : "uncertain", nextAction = failure?.Remediation ?? (local ? "correct_request_or_wait_for_capacity" : "reconcile_before_retry"), automaticReplay = false } } }), ct);
    }
    private async Task WriteAsync(TextWriter output, JsonElement frame, CancellationToken ct)
    {
        await _outputGate.WaitAsync(ct);
        try { await output.WriteLineAsync(JsonSerializer.Serialize(frame).AsMemory(), ct); await output.FlushAsync(ct); }
        finally { _outputGate.Release(); }
    }
    private sealed class Pending(JsonElement id, CancellationToken ct) : IDisposable
    {
        public JsonElement Id { get; } = id;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(ct);
        public void Cancel() { lock (Cancellation) { if (!_disposed) Cancellation.Cancel(); } }
        private bool _disposed;
        public void Dispose() { lock (Cancellation) { _disposed = true; Cancellation.Dispose(); } }
    }
}

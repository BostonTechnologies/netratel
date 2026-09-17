using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NetRatel.Application.Terminals;
using NetRatel.Shared.Contracts.Terminals;

namespace NetRatel.API.Services.Terminal;

public sealed class TerminalDirectTunnelRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, AgentTunnel> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DirectTerminalSession> _sessions = new(StringComparer.Ordinal);
    private readonly TerminalTimingRecorder _timing;
    private readonly TerminalTransportOptions _options;
    private readonly ILogger<TerminalDirectTunnelRegistry> _logger;
    private readonly ITerminalShadowObservationSink _shadowSink;

    public TerminalDirectTunnelRegistry(
        TerminalTimingRecorder timing,
        IOptions<TerminalTransportOptions> options,
        ILogger<TerminalDirectTunnelRegistry> logger)
        : this(timing, options, logger, NullTerminalShadowObservationSink.Instance)
    {
    }

    public TerminalDirectTunnelRegistry(
        TerminalTimingRecorder timing,
        IOptions<TerminalTransportOptions> options,
        ILogger<TerminalDirectTunnelRegistry> logger,
        ITerminalShadowObservationSink shadowSink)
    {
        _timing = timing;
        _options = options.Value;
        _logger = logger;
        _shadowSink = shadowSink;
    }

    public int ConnectedAgentCount => _agents.Values.Count(static x => x.IsOpen);
    public int ActiveSessionCount => _sessions.Values.Count(static x => !x.IsClosed);

    public bool IsTunnelConnected(string clientIdentityHex) =>
        TryGetTunnel(clientIdentityHex, out _);

    public DateTimeOffset? GetTunnelLastSeen(string clientIdentityHex) =>
        _agents.TryGetValue(NormalizeIdentity(clientIdentityHex), out var tunnel)
            ? tunnel.LastSeenUtc
            : null;

    public bool TryGet(string sessionId, out DirectTerminalSession? session) =>
        _sessions.TryGetValue(sessionId, out session);

    public TerminalSessionDto? GetDto(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session.ToDto() : null;

    public IReadOnlyList<TerminalSessionDto> ListByClient(string clientIdentityHex) =>
        _sessions.Values
            .Where(x => string.Equals(x.ClientIdentityHex, NormalizeIdentity(clientIdentityHex), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => x.ToDto())
            .ToList();

    public TerminalDiagnosticsDto? GetDiagnostics(string sessionId) =>
        _timing.GetDiagnostics(sessionId);

    public async Task<DirectTerminalSession> OpenAsync(
        string sessionId,
        string clientIdentityHex,
        string shellType,
        string payloadJson,
        int? cols,
        int? rows,
        CancellationToken ct) =>
        await OpenCoreAsync(
            sessionId,
            clientIdentityHex,
            shellType,
            payloadJson,
            cols,
            rows,
            null,
            ct).ConfigureAwait(false);

    public async Task<DirectTerminalSession> OpenAsync(
        string sessionId,
        string clientIdentityHex,
        string shellType,
        string payloadJson,
        int? cols,
        int? rows,
        TerminalShadowSessionKey shadowSession,
        CancellationToken ct)
    {
        if (!shadowSession.IsValid ||
            shadowSession.Transport != TerminalTransportKind.ApiWebSocket ||
            !string.Equals(shadowSession.TerminalSessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(shadowSession.ClientId, clientIdentityHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The terminal shadow session does not match the direct terminal request.", nameof(shadowSession));
        }

        return await OpenCoreAsync(
            sessionId,
            clientIdentityHex,
            shellType,
            payloadJson,
            cols,
            rows,
            shadowSession,
            ct).ConfigureAwait(false);
    }

    private async Task<DirectTerminalSession> OpenCoreAsync(
        string sessionId,
        string clientIdentityHex,
        string shellType,
        string payloadJson,
        int? cols,
        int? rows,
        TerminalShadowSessionKey? shadowSession,
        CancellationToken ct)
    {
        if (!TryGetTunnel(clientIdentityHex, out var tunnel))
        {
            throw new InvalidOperationException("Direct terminal tunnel is not connected for this client.");
        }

        var normalizedClient = NormalizeIdentity(clientIdentityHex);
        var session = new DirectTerminalSession(
            sessionId,
            normalizedClient,
            shellType,
            _timing,
            _logger,
            shadowSession);
        session.Cols = cols;
        session.Rows = rows;
        if (!_sessions.TryAdd(sessionId, session))
        {
            throw new InvalidOperationException($"Terminal session '{sessionId}' already exists.");
        }

        try
        {
            await tunnel.SendAsync(new TerminalDirectFrame(
                Type: TerminalDirectFrameTypes.Open,
                SessionId: sessionId,
                ClientIdentityHex: normalizedClient,
                ShellType: shellType,
                PayloadJson: payloadJson,
                Cols: cols,
                Rows: rows,
                TimingId: CreateTimingId(),
                SentUnixMs: NowMs()), ct).ConfigureAwait(false);
            session.State = "opening";
            _timing.GetOrCreate(sessionId, TerminalTransportKind.ApiWebSocket).State = "opening";
            _timing.RecordStage(sessionId, TerminalTransportKind.ApiWebSocket, "api.open.sent", 0);
            _logger.LogInformation("[TerminalDirect] Open sent session={SessionId} client={ClientIdentity} shell={ShellType}", sessionId, normalizedClient, shellType);
            ObserveLifecycle(session, TerminalShadowEventKind.SessionRequested);
            return session;
        }
        catch
        {
            _sessions.TryRemove(sessionId, out _);
            _timing.Remove(sessionId);
            throw;
        }
    }

    public async Task<ulong> SendInputAsync(string sessionId, string data, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException($"Unknown terminal session '{sessionId}'.");
        }

        if (session.IsClosed)
        {
            throw new InvalidOperationException("Terminal session is closed.");
        }

        if (!TryGetTunnel(session.ClientIdentityHex, out var tunnel))
        {
            throw new InvalidOperationException("Direct terminal tunnel is not connected for this client.");
        }

        var sequence = session.NextInputSequence();
        var started = DateTimeOffset.UtcNow;
        var control = DescribeControlInput(data);
        await tunnel.SendAsync(new TerminalDirectFrame(
            Type: TerminalDirectFrameTypes.Stdin,
            SessionId: sessionId,
            Data: data,
            Sequence: sequence,
            TimingId: CreateTimingId(),
            SentUnixMs: started.ToUnixTimeMilliseconds()), ct).ConfigureAwait(false);
        var dataBytes = Encoding.UTF8.GetByteCount(data);
        _timing.RecordFrame(sessionId, TerminalTransportKind.ApiWebSocket, "input", dataBytes);
        _timing.RecordStage(sessionId, TerminalTransportKind.ApiWebSocket, "api.input.dispatch", ElapsedMs(started));
        if (!string.IsNullOrWhiteSpace(control))
        {
            _logger.LogInformation(
                "[TerminalDirect] api.input.dispatch session={SessionId} control={Control} seq={Sequence} bytes={Bytes}",
                sessionId,
                control,
                sequence,
                dataBytes);
        }

        ObserveStream(
            session,
            TerminalShadowEventKind.InputObserved,
            TerminalShadowStreamType.StandardInput,
            sequence,
            dataBytes);

        return sequence;
    }

    public async Task ResizeAsync(string sessionId, int cols, int rows, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        session.Cols = cols;
        session.Rows = rows;
        session.Touch();

        if (session.IsClosed || !TryGetTunnel(session.ClientIdentityHex, out var tunnel))
        {
            return;
        }

        var sequence = session.NextControlSequence();
        var timingId = CreateTimingId();
        await tunnel.SendAsync(new TerminalDirectFrame(
            Type: TerminalDirectFrameTypes.Resize,
            SessionId: sessionId,
            Cols: cols,
            Rows: rows,
            Sequence: sequence,
            TimingId: timingId,
            SentUnixMs: NowMs()), ct).ConfigureAwait(false);
        _logger.LogInformation(
            "[TerminalDirect] Resize dispatched session={SessionId} client={ClientIdentity} seq={Sequence} timing={TimingId} cols={Cols} rows={Rows}",
            sessionId,
            session.ClientIdentityHex,
            sequence,
            timingId,
            cols,
            rows);
        ObserveStream(
            session,
            TerminalShadowEventKind.ResizeObserved,
            TerminalShadowStreamType.Resize,
            sequence,
            payloadLength: 0,
            cols,
            rows);
    }

    public async Task CloseAsync(string sessionId, string? reason, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException($"Unknown terminal session '{sessionId}'.");
        }

        if (session.IsClosed)
        {
            session.WriteClose(reason ?? "Terminal session is already closed.");
            ObserveLifecycle(
                session,
                TerminalShadowEventKind.SessionClosed,
                TerminalShadowCloseKind.RemoteClosed);
            return;
        }

        session.State = "closing";
        session.CloseReason = reason;
        session.Touch();

        if (TryGetTunnel(session.ClientIdentityHex, out var tunnel))
        {
            await tunnel.SendAsync(new TerminalDirectFrame(
                Type: TerminalDirectFrameTypes.Close,
                SessionId: sessionId,
                Reason: reason ?? "API close requested.",
                TimingId: CreateTimingId(),
                SentUnixMs: NowMs()), ct).ConfigureAwait(false);
            ObserveLifecycle(
                session,
                TerminalShadowEventKind.SessionCloseRequested,
                TerminalShadowCloseKind.OperatorRequested);
        }
        else
        {
            session.WriteClose("Direct terminal tunnel disconnected.");
            ObserveLifecycle(
                session,
                TerminalShadowEventKind.SessionFailed,
                TerminalShadowCloseKind.TransportLost);
        }
    }

    public async Task HandleAgentWebSocketAsync(HttpContext httpContext, CancellationToken ct)
    {
        using var socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        AgentTunnel? tunnel = null;
        Task? keepAliveTask = null;
        using var tunnelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await foreach (var frame in ReceiveFramesAsync(socket, ct).ConfigureAwait(false))
            {
                if (string.Equals(frame.Type, TerminalDirectFrameTypes.Hello, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(frame.ClientIdentityHex))
                    {
                        await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "missing-client-identity").ConfigureAwait(false);
                        return;
                    }

                    var clientIdentity = NormalizeIdentity(frame.ClientIdentityHex);
                    tunnel = new AgentTunnel(clientIdentity, socket);
                    if (_agents.TryGetValue(clientIdentity, out var previous))
                    {
                        previous.MarkReplaced();
                    }

                    _agents[clientIdentity] = tunnel;
                    _logger.LogInformation("[TerminalDirect] Agent tunnel connected client={ClientIdentity}", clientIdentity);
                    await tunnel.SendAsync(new TerminalDirectFrame(TerminalDirectFrameTypes.Ack, ClientIdentityHex: clientIdentity, SentUnixMs: NowMs()), ct)
                        .ConfigureAwait(false);
                    keepAliveTask = RunKeepAliveAsync(tunnel, tunnelCts.Token);
                    continue;
                }

                if (tunnel is null)
                {
                    await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "hello-required").ConfigureAwait(false);
                    return;
                }

                tunnel.Touch();
                HandleAgentFrame(tunnel, frame);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "[TerminalDirect] Agent tunnel websocket closed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TerminalDirect] Agent tunnel failed.");
        }
        finally
        {
            try { await tunnelCts.CancelAsync().ConfigureAwait(false); } catch { }
            if (keepAliveTask is not null)
            {
                try { await keepAliveTask.ConfigureAwait(false); } catch { }
            }

            if (tunnel is not null)
            {
                if (_agents.TryGetValue(tunnel.ClientIdentityHex, out var current) && ReferenceEquals(current, tunnel))
                {
                    _agents.TryRemove(tunnel.ClientIdentityHex, out _);
                }

                CloseClientSessions(tunnel.ClientIdentityHex, "Direct terminal tunnel disconnected.");
                _logger.LogWarning("[TerminalDirect] Agent tunnel disconnected client={ClientIdentity}", tunnel.ClientIdentityHex);
            }
        }
    }

    private Task RunKeepAliveAsync(AgentTunnel tunnel, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var intervalSeconds = Math.Max(_options.AgentTunnelPingSeconds, 5);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
            while (!ct.IsCancellationRequested && tunnel.IsOpen && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await tunnel.SendAsync(new TerminalDirectFrame(
                    Type: TerminalDirectFrameTypes.Ping,
                    ClientIdentityHex: tunnel.ClientIdentityHex,
                    SentUnixMs: NowMs()), ct).ConfigureAwait(false);
            }
        }, ct);

    private void HandleAgentFrame(AgentTunnel tunnel, TerminalDirectFrame frame)
    {
        if (string.IsNullOrWhiteSpace(frame.SessionId) ||
            !_sessions.TryGetValue(frame.SessionId, out var session))
        {
            return;
        }

        switch (frame.Type.ToLowerInvariant())
        {
            case TerminalDirectFrameTypes.Opened:
                session.State = "active";
                session.Backend = string.IsNullOrWhiteSpace(frame.Backend) ? "agent-pty" : frame.Backend;
                session.Touch();
                _timing.GetOrCreate(session.SessionId, TerminalTransportKind.ApiWebSocket).State = "active";
                _timing.RecordStage(session.SessionId, TerminalTransportKind.ApiWebSocket, "agent.opened.api", frame.SentUnixMs.HasValue ? NowMs() - frame.SentUnixMs.Value : 0);
                _logger.LogInformation(
                    "[TerminalDirect] Agent opened session={SessionId} backend={Backend}",
                    session.SessionId,
                    session.Backend);
                ObserveLifecycle(session, TerminalShadowEventKind.SessionOpened);
                break;
            case TerminalDirectFrameTypes.Output:
                var direction = string.IsNullOrWhiteSpace(frame.Direction) ? "stdout" : frame.Direction;
                var data = frame.Data ?? string.Empty;
                var dataBytes = Encoding.UTF8.GetByteCount(data);
                _logger.LogInformation(
                    "[TerminalDirect] Direct output frame received session={SessionId} client={ClientIdentity} direction={Direction} seq={Sequence} timing={TimingId} chars={Chars} bytes={Bytes}",
                    session.SessionId,
                    session.ClientIdentityHex,
                    direction,
                    frame.Sequence,
                    frame.TimingId ?? string.Empty,
                    data.Length,
                    dataBytes);
                _timing.RecordStage(session.SessionId, TerminalTransportKind.ApiWebSocket, "agent.output.frame", 0);
                var channelWriteStarted = DateTimeOffset.UtcNow;
                var (firstOutput, channelWritten) = session.WriteData(direction, frame.Sequence, data, frame.TimingId);
                if (channelWritten)
                {
                    _timing.RecordStage(session.SessionId, TerminalTransportKind.ApiWebSocket, "api.output.channel", ElapsedMs(channelWriteStarted));
                    if (frame.Sequence.HasValue)
                    {
                        ObserveStream(
                            session,
                            TerminalShadowEventKind.OutputObserved,
                            string.Equals(direction, "stderr", StringComparison.OrdinalIgnoreCase)
                                ? TerminalShadowStreamType.StandardError
                                : TerminalShadowStreamType.StandardOutput,
                            frame.Sequence.Value,
                            dataBytes);
                    }
                }

                _timing.RecordFrame(session.SessionId, TerminalTransportKind.ApiWebSocket, "output", dataBytes);
                if (frame.SentUnixMs.HasValue)
                {
                    _timing.RecordStage(session.SessionId, TerminalTransportKind.ApiWebSocket, "agent.output.api", NowMs() - frame.SentUnixMs.Value);
                }

                if (firstOutput)
                {
                    _logger.LogInformation(
                        "[TerminalDirect] First output session={SessionId} direction={Direction} seq={Sequence} bytes={Bytes}",
                        session.SessionId,
                        direction,
                        frame.Sequence,
                        dataBytes);
                }
                break;
            case TerminalDirectFrameTypes.Closed:
                session.WriteClose(frame.Reason ?? "Shell exited.");
                _timing.GetOrCreate(session.SessionId, TerminalTransportKind.ApiWebSocket).State = "closed";
                _timing.RecordStage(session.SessionId, TerminalTransportKind.ApiWebSocket, "agent.closed.api", 0);
                _logger.LogInformation("[TerminalDirect] Agent closed session={SessionId} reason={Reason}", session.SessionId, frame.Reason);
                ObserveLifecycle(
                    session,
                    TerminalShadowEventKind.SessionClosed,
                    TerminalShadowCloseKind.RemoteClosed);
                break;
            case TerminalDirectFrameTypes.Error:
                session.WriteClose(frame.Reason ?? "Agent terminal error.");
                _timing.GetOrCreate(session.SessionId, TerminalTransportKind.ApiWebSocket).State = "closed";
                _timing.RecordStage(session.SessionId, TerminalTransportKind.ApiWebSocket, "agent.error.api", 0);
                _logger.LogWarning("[TerminalDirect] Agent error session={SessionId} reason={Reason}", session.SessionId, frame.Reason);
                ObserveLifecycle(
                    session,
                    TerminalShadowEventKind.SessionFailed,
                    TerminalShadowCloseKind.AgentError);
                break;
            case TerminalDirectFrameTypes.Pong:
                tunnel.Touch();
                break;
            case TerminalDirectFrameTypes.Ack:
                if (string.Equals(frame.Direction, TerminalDirectFrameDirections.AgentInputPty, StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(frame.Data, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var elapsedMs))
                {
                    _timing.RecordStage(session.SessionId, TerminalTransportKind.ApiWebSocket, "agent.input.pty", elapsedMs);
                }
                else if (string.Equals(frame.Direction, TerminalDirectFrameDirections.AgentResizeApplied, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(frame.Direction, TerminalDirectFrameDirections.AgentResizeFailed, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(frame.Direction, TerminalDirectFrameDirections.AgentResizeUnsupported, StringComparison.OrdinalIgnoreCase))
                {
                    var result = frame.Direction switch
                    {
                        TerminalDirectFrameDirections.AgentResizeApplied => "applied",
                        TerminalDirectFrameDirections.AgentResizeUnsupported => "unsupported",
                        _ => "failed"
                    };
                    if (frame.Cols.HasValue)
                    {
                        session.Cols = frame.Cols.Value;
                    }

                    if (frame.Rows.HasValue)
                    {
                        session.Rows = frame.Rows.Value;
                    }

                    session.Backend = string.IsNullOrWhiteSpace(frame.Backend) ? session.Backend : frame.Backend;
                    session.Touch();
                    _timing.RecordStage(session.SessionId, TerminalTransportKind.ApiWebSocket, $"agent.resize.{result}", frame.SentUnixMs.HasValue ? NowMs() - frame.SentUnixMs.Value : 0);
                    _logger.LogInformation(
                        "[TerminalDirect] Resize ack session={SessionId} client={ClientIdentity} seq={Sequence} timing={TimingId} backend={Backend} result={Result} cols={Cols} rows={Rows} detail={Detail}",
                        session.SessionId,
                        session.ClientIdentityHex,
                        frame.Sequence,
                        frame.TimingId ?? string.Empty,
                        session.Backend,
                        result,
                        frame.Cols,
                        frame.Rows,
                        frame.Data ?? string.Empty);
                }

                break;
        }
    }

    private bool TryGetTunnel(string clientIdentityHex, out AgentTunnel tunnel)
    {
        var normalized = NormalizeIdentity(clientIdentityHex);
        if (_agents.TryGetValue(normalized, out tunnel!) &&
            tunnel.IsOpen &&
            DateTimeOffset.UtcNow - tunnel.LastSeenUtc < TimeSpan.FromSeconds(Math.Max(_options.AgentTunnelStaleSeconds, 5)))
        {
            return true;
        }

        tunnel = null!;
        return false;
    }

    private void CloseClientSessions(string clientIdentityHex, string reason)
    {
        foreach (var session in _sessions.Values.Where(x => string.Equals(x.ClientIdentityHex, clientIdentityHex, StringComparison.OrdinalIgnoreCase)))
        {
            session.WriteClose(reason);
            ObserveLifecycle(
                session,
                TerminalShadowEventKind.SessionFailed,
                TerminalShadowCloseKind.TransportLost);
        }
    }

    private void ObserveLifecycle(
        DirectTerminalSession session,
        TerminalShadowEventKind kind,
        TerminalShadowCloseKind closeKind = TerminalShadowCloseKind.None)
    {
        if (session.ShadowSession is not { } shadowSession)
        {
            return;
        }

        _shadowSink.TryEnqueue(new TerminalShadowEvent(
            shadowSession,
            kind,
            TerminalShadowStreamType.Lifecycle,
            DateTimeOffset.UtcNow,
            CloseKind: closeKind));
    }

    private void ObserveStream(
        DirectTerminalSession session,
        TerminalShadowEventKind kind,
        TerminalShadowStreamType streamType,
        ulong sequence,
        int payloadLength,
        int? columns = null,
        int? rows = null)
    {
        if (session.ShadowSession is not { } shadowSession)
        {
            return;
        }

        _shadowSink.TryEnqueue(new TerminalShadowEvent(
            shadowSession,
            kind,
            streamType,
            DateTimeOffset.UtcNow,
            sequence,
            payloadLength,
            columns,
            rows));
    }

    private static async IAsyncEnumerable<TerminalDirectFrame> ReceiveFramesAsync(
        WebSocket socket,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[16384];
        var message = new ArrayBufferWriter<byte>();

        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            message.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    yield break;
                }

                if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                {
                    message.Write(buffer.AsSpan(0, result.Count));
                }
            }
            while (!result.EndOfMessage);

            if (message.WrittenCount == 0)
            {
                continue;
            }

            var frame = JsonSerializer.Deserialize<TerminalDirectFrame>(message.WrittenSpan, JsonOptions);
            if (frame is not null)
            {
                yield return frame;
            }
        }
    }

    private static async Task CloseSocketAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(status, reason, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static string NormalizeIdentity(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

    private static string CreateTimingId() => Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture);
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static double ElapsedMs(DateTimeOffset started) => Math.Max(0, (DateTimeOffset.UtcNow - started).TotalMilliseconds);

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

    private sealed class AgentTunnel
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private volatile bool _replaced;

        public AgentTunnel(string clientIdentityHex, WebSocket socket)
        {
            ClientIdentityHex = clientIdentityHex;
            _socket = socket;
            LastSeenUtc = DateTimeOffset.UtcNow;
        }

        public string ClientIdentityHex { get; }
        public DateTimeOffset LastSeenUtc { get; private set; }
        public bool IsOpen => !_replaced && _socket.State == WebSocketState.Open;

        public void Touch() => LastSeenUtc = DateTimeOffset.UtcNow;
        public void MarkReplaced() => _replaced = true;

        public async Task SendAsync(TerminalDirectFrame frame, CancellationToken ct)
        {
            if (!IsOpen)
            {
                throw new InvalidOperationException("Agent tunnel is not open.");
            }

            var json = JsonSerializer.Serialize(frame, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                Touch();
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }

    public sealed class DirectTerminalSession
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly TerminalTimingRecorder _timing;
        private readonly ILogger _logger;
        private bool _firstOutputWritten;

        public DirectTerminalSession(
            string sessionId,
            string clientIdentityHex,
            string shellType,
            TerminalTimingRecorder timing,
            ILogger logger,
            TerminalShadowSessionKey? shadowSession = null)
        {
            SessionId = sessionId;
            ClientIdentityHex = clientIdentityHex;
            ShellType = shellType;
            _timing = timing;
            _logger = logger;
            ShadowSession = shadowSession;
            OutputChannel = Channel.CreateUnbounded<TerminalStreamMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            CreatedUtc = DateTimeOffset.UtcNow;
            LastActivityUtc = CreatedUtc;
        }

        public string SessionId { get; }
        public string ClientIdentityHex { get; }
        public string ShellType { get; }
        public TerminalShadowSessionKey? ShadowSession { get; }
        public Channel<TerminalStreamMessage> OutputChannel { get; }
        public DateTimeOffset CreatedUtc { get; }
        public DateTimeOffset LastActivityUtc { get; private set; }
        public ulong NextInputSequenceValue { get; private set; }
        public ulong NextControlSequenceValue { get; private set; }
        public string State { get; set; } = "opening";
        public string? CloseReason { get; set; }
        public string Backend { get; set; } = "agent-pty";
        public bool IsClosed => string.Equals(State, "closed", StringComparison.OrdinalIgnoreCase);
        public int? Cols { get; set; }
        public int? Rows { get; set; }
        public object Sync { get; } = new();

        public void Touch() => LastActivityUtc = DateTimeOffset.UtcNow;

        public ulong NextInputSequence()
        {
            lock (Sync)
            {
                NextInputSequenceValue++;
                Touch();
                return NextInputSequenceValue;
            }
        }

        public ulong NextControlSequence()
        {
            lock (Sync)
            {
                NextControlSequenceValue++;
                Touch();
                return NextControlSequenceValue;
            }
        }

        public (bool FirstOutput, bool ChannelWritten) WriteData(string direction, ulong? sequence, string data, string? timingId)
        {
            if (string.IsNullOrEmpty(data))
            {
                return (false, false);
            }

            var key = $"data:{direction}:{sequence?.ToString() ?? string.Empty}:{data}";
            lock (Sync)
            {
                if (!_seen.Add(key))
                {
                    return (false, false);
                }

                State = "active";
                Touch();
                _timing.GetOrCreate(SessionId, TerminalTransportKind.ApiWebSocket).State = "active";
                var message = new TerminalStreamMessage(
                    Kind: "data",
                    Data: data,
                    Direction: direction,
                    Sequence: sequence,
                    TimingId: timingId);
                var channelWritten = OutputChannel.Writer.TryWrite(message);
                _logger.LogInformation(
                    "[TerminalDirect] Output written to session channel session={SessionId} direction={Direction} seq={Sequence} timing={TimingId} chars={Chars} bytes={Bytes} accepted={Accepted}",
                    SessionId,
                    direction,
                    sequence,
                    timingId ?? string.Empty,
                    data.Length,
                    Encoding.UTF8.GetByteCount(data),
                    channelWritten);
                var firstOutput = !_firstOutputWritten;
                _firstOutputWritten = true;
                return (firstOutput, channelWritten);
            }
        }

        public void WriteClose(string? reason)
        {
            var key = $"close:{reason ?? string.Empty}";
            lock (Sync)
            {
                if (!_seen.Add(key))
                {
                    return;
                }

                State = "closed";
                CloseReason = reason;
                Touch();
                _timing.GetOrCreate(SessionId, TerminalTransportKind.ApiWebSocket).State = "closed";
                OutputChannel.Writer.TryWrite(new TerminalStreamMessage(
                    Kind: "close",
                    Reason: reason));
                OutputChannel.Writer.TryComplete();
            }
        }

        public TerminalSessionDto ToDto() =>
            new(
                SessionId,
                ClientIdentityHex,
                ShellType,
                State,
                true,
                CreatedUtc.ToUnixTimeMilliseconds(),
                LastActivityUtc.ToUnixTimeMilliseconds(),
                CloseReason,
                Backend,
                Cols,
                Rows,
                new[] { "stdin-websocket", "agent-api-websocket", "resize-control", "timing-diagnostics" },
                TerminalTransportKind.ApiWebSocket);
    }
}

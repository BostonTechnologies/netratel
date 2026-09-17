using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Terminal;
using static NetRatel.Client.Service.Terminal.Terminal;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Owns agent-side PTYs for the fenced terminal authority stream. This path
/// deliberately has no SpacetimeDB or direct-WebSocket fallback: a failed
/// gateway session retries the same authenticated stream and preserves only
/// PTYs that still belong to the same authenticated presence fence.
/// </summary>
public sealed class AgentTerminalGatewayClient : IDisposable
{
    internal const int CleanupWorkerCount = 4;
    internal const int CleanupQueueCapacity = 128;
    private const int MaximumPendingClosedFrames = 256;
    private const int OutputDropLogInterval = 128;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FrameWriteTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconciliationDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PendingClosedLifetime = TimeSpan.FromMinutes(2);

    private readonly GatewayClientOptions _options;
    private readonly IReadOnlyList<string> _terminalShells;
    private readonly Action<string> _log;
    private readonly Func<Uri, GrpcChannel> _createChannel;
    private readonly TerminalHostFactory _hostFactory;
    private readonly ConcurrentDictionary<string, GatewayTerminalSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TerminalSessionKey, GatewayTerminalSession> _closingSessions = new();
    private readonly ConcurrentDictionary<TerminalSessionKey, PendingClosedTerminal> _pendingClosed = new();
    // The bounded channel controls scheduled cleanup concurrency. Sessions that
    // arrive while it is full remain in this state registry until a worker
    // frees a slot; this avoids creating unbounded fallback tasks or disposing
    // a PTY synchronously from the response loop.
    private readonly ConcurrentDictionary<GatewayTerminalSession, CleanupWork> _pendingCleanup = new();
    private readonly Channel<CleanupWork> _cleanupQueue = Channel.CreateBounded<CleanupWork>(
        new BoundedChannelOptions(CleanupQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false
        });
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _presenceSync = new();
    private readonly Task[] _cleanupWorkers;
    private GatewayPresenceFence? _activePresenceFence;
    private CancellationToken _activePresenceOwnerToken;
    private GatewayWriter? _currentWriter;
    private long _deferredCleanupCount;
    private int _disposed;

    public AgentTerminalGatewayClient(
        GatewayClientOptions options,
        TerminalHostOptions hostOptions,
        IReadOnlyList<string> terminalShells,
        Action<string> log,
        Func<Uri, GrpcChannel>? createChannel = null)
    {
        _options = options;
        _terminalShells = terminalShells;
        _log = log;
        _createChannel = createChannel ?? (endpoint => GrpcChannel.ForAddress(endpoint));
        _hostFactory = new TerminalHostFactory(hostOptions);
        _cleanupWorkers = Enumerable.Range(0, CleanupWorkerCount)
            .Select(_ => Task.Run(ProcessCleanupQueueAsync))
            .ToArray();
    }

    public async Task RunForPresenceSessionAsync(GatewayPresenceSession session, string accessToken, CancellationToken stoppingToken)
    {
        if (!_options.TerminalGatewayEnabled || !_options.TerminalAuthorityEnabled)
        {
            return;
        }

        if (!Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            _log("Terminal gateway is disabled because Gateway:Endpoint is not an absolute HTTPS URL.");
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _disposeCts.Token);
        var presenceFence = GatewayPresenceFence.From(session);
        // Keep the original owner token: the linked source is disposed if this
        // child exits early, before its parent presence session is cancelled.
        if (!TryPreparePresenceFence(presenceFence, stoppingToken))
        {
            _log("Terminal gateway session was ignored because its presence owner was cancelled or superseded.");
            return;
        }

        var retryDelay = InitialRetryDelay;
        while (!linked.IsCancellationRequested)
        {
            try
            {
                ThrowIfPresenceFenceSuperseded(presenceFence);
                await RunStreamAsync(endpoint, session, presenceFence, accessToken, linked.Token).ConfigureAwait(false);
                throw new RpcException(new Status(StatusCode.Unavailable, "Terminal gateway response stream ended."));
            }
            catch (PresenceFenceSupersededException)
            {
                _log("Terminal gateway session stopped because its presence fence was superseded.");
                break;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                _log("Terminal gateway session stopped with its presence owner.");
                break;
            }
            catch (RpcException exception) when (linked.IsCancellationRequested && exception.StatusCode == StatusCode.Cancelled)
            {
                _log("Terminal gateway session stopped with its presence owner.");
                break;
            }
            catch (Exception exception) when (exception is RpcException or HttpRequestException or IOException)
            {
                if (linked.IsCancellationRequested)
                {
                    _log("Terminal gateway session stopped with its presence owner.");
                    break;
                }

                _log($"Terminal gateway session failed: {exception.GetType().Name}: {exception.Message}. Retrying in {retryDelay.TotalSeconds:0}s.");
                try
                {
                    await Task.Delay(retryDelay, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    _log("Terminal gateway session stopped with its presence owner.");
                    break;
                }

                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
            }
        }
    }

    private async Task RunStreamAsync(
        Uri endpoint,
        GatewayPresenceSession session,
        GatewayPresenceFence presenceFence,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        using var channel = _createChannel(endpoint);
        var client = new AgentTerminalGateway.AgentTerminalGatewayClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {accessToken}" } };
        using var call = client.Connect(headers, cancellationToken: streamCancellation.Token);
        using var writer = new GatewayWriter(call.RequestStream, session, _options.ProtocolVersion);

        await writer.WriteHelloAsync(new AgentTerminalFrame
        {
            Hello = new AgentTerminalHello
            {
                Capabilities = { "pty", "stdin", "stdout", "resize", "reconnect", "idempotent-close" },
                TerminalCapability = new TerminalCapability { Supported = true, AvailableShells = { _terminalShells } }
            }
        }, streamCancellation.Token).ConfigureAwait(false);

        if (!await call.ResponseStream.MoveNext(streamCancellation.Token).ConfigureAwait(false) ||
            call.ResponseStream.Current.PayloadCase != GatewayTerminalFrame.PayloadOneofCase.Accepted)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Terminal gateway closed before accepting the session."));
        }

        ValidateAccepted(call.ResponseStream.Current, session);
        _log($"Terminal gateway admitted. authority={call.ResponseStream.Current.Accepted.TerminalAuthority}.");

        ThrowIfPresenceFenceSuperseded(presenceFence);
        writer.Start(streamCancellation.Token);
        AttachCurrentWriter(presenceFence, writer);

        // Start the reader before reconciliation. A stale PTY must never keep
        // Start, Close, or presence-fence decisions waiting behind its
        // reannouncement or shutdown work.
        var responses = ProcessResponsesAsync(call.ResponseStream, session, presenceFence, writer, streamCancellation.Token);
        AttachOwnedSessions(presenceFence, writer);
        var reconciliation = ReconcileAsync(presenceFence, writer, streamCancellation.Token);

        try
        {
            var completed = await Task.WhenAny(responses, writer.Completion).ConfigureAwait(false);
            if (ReferenceEquals(completed, writer.Completion))
            {
                await writer.Completion.ConfigureAwait(false);
                throw new RpcException(new Status(StatusCode.Unavailable, "Terminal gateway outbound stream completed unexpectedly."));
            }

            await responses.ConfigureAwait(false);
        }
        finally
        {
            streamCancellation.Cancel();
            DetachCurrentWriter(presenceFence, writer);
            DetachOwnedSessions(presenceFence, writer);
            await ObserveReconciliationAsync(reconciliation, streamCancellation.Token).ConfigureAwait(false);
            await ObserveWriterStopAsync(writer, streamCancellation.Token).ConfigureAwait(false);
        }
    }

    private async Task ProcessResponsesAsync(
        IAsyncStreamReader<GatewayTerminalFrame> responseStream,
        GatewayPresenceSession session,
        GatewayPresenceFence presenceFence,
        GatewayWriter writer,
        CancellationToken cancellationToken)
    {
        ulong lastServerSequence = 0;
        while (await responseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            // A late child from the previous presence owner can overlap a
            // newly admitted fence briefly. It must stop before it can route
            // any frame by a reused session ID into the replacement owner.
            ThrowIfPresenceFenceSuperseded(presenceFence);

            var frame = responseStream.Current;
            ValidateFrame(frame, session);
            if (frame.Sequence == 0 || frame.Sequence <= lastServerSequence)
            {
                throw new RpcException(new Status(StatusCode.DataLoss, "Terminal gateway returned a stale or out-of-order frame."));
            }

            lastServerSequence = frame.Sequence;
            switch (frame.PayloadCase)
            {
                case GatewayTerminalFrame.PayloadOneofCase.Start:
                    await OpenAsync(frame.Start, writer, presenceFence, cancellationToken).ConfigureAwait(false);
                    break;
                case GatewayTerminalFrame.PayloadOneofCase.Input:
                    await WriteInputAsync(frame.Input, presenceFence, cancellationToken).ConfigureAwait(false);
                    break;
                case GatewayTerminalFrame.PayloadOneofCase.Resize:
                    await ResizeAsync(frame.Resize, presenceFence, cancellationToken).ConfigureAwait(false);
                    break;
                case GatewayTerminalFrame.PayloadOneofCase.Close:
                    CloseAsync(frame.Close, writer, presenceFence);
                    break;
                default:
                    throw new RpcException(new Status(StatusCode.DataLoss, "Terminal gateway returned an unsupported frame."));
            }
        }

        throw new RpcException(new Status(StatusCode.Unavailable, "Terminal gateway response stream completed."));
    }

    private async Task ReconcileAsync(
        GatewayPresenceFence presenceFence,
        GatewayWriter writer,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReconciliationDeadline);

        try
        {
            foreach (var session in _sessions.Values
                         .Where(candidate => candidate.Owns(presenceFence) && candidate.IsStarted && candidate.IsAttached(writer))
                         .OrderBy(candidate => candidate.SessionId, StringComparer.Ordinal))
            {
                ThrowIfPresenceFenceSuperseded(presenceFence);
                if (!IsCurrentSession(session))
                {
                    continue;
                }

                await session.QueueOpenedAsync(writer, deadline.Token).ConfigureAwait(false);
            }

            foreach (var pending in _pendingClosed.Values
                         .Where(candidate => candidate.Key.PresenceFence == presenceFence)
                         .OrderBy(candidate => candidate.CreatedAtUtc))
            {
                ThrowIfPresenceFenceSuperseded(presenceFence);
                await writer.QueueControlAsync(pending.ToFrame(), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (PresenceFenceSupersededException)
        {
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            _log("Terminal gateway reconciliation reached its bounded deadline; a replacement stream will retry retained sessions.");
        }
    }

    private async Task OpenAsync(
        TerminalSessionStart start,
        GatewayWriter writer,
        GatewayPresenceFence presenceFence,
        CancellationToken cancellationToken)
    {
        ThrowIfPresenceFenceSuperseded(presenceFence);

        if (string.IsNullOrWhiteSpace(start.SessionId) || start.Generation == 0 ||
            start.Columns is < 40 or > 300 || start.Rows is < 10 or > 120 ||
            string.IsNullOrWhiteSpace(start.ShellType))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Terminal gateway start frame is invalid."));
        }

        var sessionKey = new TerminalSessionKey(start.SessionId, start.Generation, presenceFence);
        if (_closingSessions.ContainsKey(sessionKey))
        {
            // A replayed Start can race the exact Close that was already
            // accepted. Do not recreate the PTY; the lifecycle worker will
            // acknowledge the Close through the current writer.
            return;
        }

        if (_pendingClosed.TryGetValue(sessionKey, out var alreadyClosed))
        {
            QueueControl(writer, alreadyClosed.ToFrame(), "closed terminal reannouncement");
            return;
        }

        if (_sessions.TryGetValue(start.SessionId, out var existing))
        {
            if (!existing.Owns(presenceFence))
            {
                // Do not let a stale response handler remove a replacement
                // session merely because the server reused its ID. A changed
                // active fence is an expected hand-off; a mismatched session
                // while this fence remains active is protocol-invalid.
                ThrowIfPresenceFenceSuperseded(presenceFence);
                throw new RpcException(new Status(StatusCode.Aborted, "Terminal gateway start references a fenced session."));
            }

            if (existing.Generation != start.Generation)
            {
                throw new RpcException(new Status(StatusCode.Aborted, "Terminal gateway attempted to reuse a fenced session ID."));
            }

            existing.Attach(writer);
            if (existing.IsStarted)
            {
                existing.TryQueueOpened();
            }

            return;
        }

        var shell = start.ShellType.Trim().ToLowerInvariant();
        if (!_terminalShells.Contains(shell, StringComparer.OrdinalIgnoreCase))
        {
            QueueControl(
                writer,
                FailedFrame(start.SessionId, start.Generation, "terminal_shell_unavailable", $"Shell '{shell}' is not available on this agent."),
                "terminal shell failure");
            return;
        }

        var payload = new TerminalOpenPayload
        {
            ShellType = start.ShellType,
            Cols = checked((int)start.Columns),
            Rows = checked((int)start.Rows),
            WorkingDirectory = string.IsNullOrWhiteSpace(start.WorkingDirectory) ? null : start.WorkingDirectory
        };

        IReadOnlyList<ITerminalHostSession> candidates;
        try
        {
            candidates = _hostFactory.CreateCandidates(payload);
        }
        catch (Exception exception)
        {
            QueueControl(
                writer,
                FailedFrame(start.SessionId, start.Generation, "terminal_shell_unavailable", exception.Message),
                "terminal shell failure");
            return;
        }

        Exception? lastFailure = null;
        foreach (var candidate in candidates)
        {
            var terminalSession = new GatewayTerminalSession(
                start.SessionId,
                start.Generation,
                presenceFence,
                candidate,
                _log);
            candidate.Exited += reason => HandleExited(terminalSession, reason);

            if (!TryAddActiveSession(terminalSession))
            {
                if (_sessions.TryGetValue(start.SessionId, out var current) && current.Owns(presenceFence) && current.Generation == start.Generation)
                {
                    current.Attach(writer);
                    if (current.IsStarted)
                    {
                        current.TryQueueOpened();
                    }
                }

                terminalSession.Fence();
                QueueCleanup(terminalSession, "terminal_session_duplicate", reportClosed: false);
                return;
            }

            terminalSession.Attach(writer);
            try
            {
                _log($"client.terminal.pty.spawn.started session={terminalSession.SessionId} generation={terminalSession.Generation} backend={candidate.Backend}.");
                await candidate.StartAsync(
                    chunk => terminalSession.WriteOutputAsync(chunk.Data, CancellationToken.None),
                    terminalSession.Cancellation.Token).ConfigureAwait(false);

                if (!IsCurrentSession(terminalSession) || terminalSession.IsClosing)
                {
                    return;
                }

                terminalSession.MarkStarted();
                if (!terminalSession.TryQueueOpened())
                {
                    _log($"Terminal gateway opened notification is pending a replacement writer session={terminalSession.SessionId} generation={terminalSession.Generation}.");
                }

                _log($"client.terminal.pty.spawn.completed session={terminalSession.SessionId} generation={terminalSession.Generation} backend={candidate.Backend}.");
                _log($"client.terminal.opened.sent session={terminalSession.SessionId} generation={terminalSession.Generation} backend={candidate.Backend}.");
                return;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                if (!TryRemoveActiveSession(terminalSession))
                {
                    return;
                }

                terminalSession.Fence();
                QueueCleanup(terminalSession, "terminal_spawn_failed", reportClosed: false);
                _log($"Terminal gateway PTY backend failed session={start.SessionId} backend={candidate.Backend}: {exception.GetType().Name}: {exception.Message}. Trying the next backend.");
            }
        }

        QueueControl(
            writer,
            FailedFrame(start.SessionId, start.Generation, "terminal_spawn_failed", lastFailure?.Message ?? "The terminal process could not be started."),
            "terminal spawn failure");
    }

    private async Task WriteInputAsync(
        TerminalInput input,
        GatewayPresenceFence presenceFence,
        CancellationToken cancellationToken)
    {
        ThrowIfPresenceFenceSuperseded(presenceFence);
        if (!_sessions.TryGetValue(input.SessionId, out var session) || session.Generation != input.Generation ||
            !session.Owns(presenceFence) || !session.IsStarted || session.IsClosing || input.Content.Length is 0 or > 16 * 1024)
        {
            ThrowIfPresenceFenceSuperseded(presenceFence);
            throw new RpcException(new Status(StatusCode.Aborted, "Terminal gateway input references an unknown, fenced, or invalid session."));
        }

        await session.Host.WriteInputAsync(input.Content.ToStringUtf8(), session.Cancellation.Token).ConfigureAwait(false);
    }

    private async Task ResizeAsync(
        TerminalResize resize,
        GatewayPresenceFence presenceFence,
        CancellationToken cancellationToken)
    {
        ThrowIfPresenceFenceSuperseded(presenceFence);
        if (!_sessions.TryGetValue(resize.SessionId, out var session) || session.Generation != resize.Generation ||
            !session.Owns(presenceFence) || !session.IsStarted || session.IsClosing || resize.Columns is < 1 or > 512 || resize.Rows is < 1 or > 512)
        {
            ThrowIfPresenceFenceSuperseded(presenceFence);
            throw new RpcException(new Status(StatusCode.Aborted, "Terminal gateway resize references an unknown, fenced, or invalid session."));
        }

        var result = await session.Host.ResizeAsync(
            checked((int)resize.Columns),
            checked((int)resize.Rows),
            session.Cancellation.Token).ConfigureAwait(false);
        if (!session.TryQueueResizeApplied(resize, result))
        {
            _log($"Terminal gateway resize acknowledgement is pending a replacement writer session={session.SessionId} generation={session.Generation}.");
        }
    }

    private void CloseAsync(TerminalSessionClose close, GatewayWriter writer, GatewayPresenceFence presenceFence)
    {
        ThrowIfPresenceFenceSuperseded(presenceFence);
        var sessionKey = new TerminalSessionKey(close.SessionId, close.Generation, presenceFence);
        if (_pendingClosed.TryGetValue(sessionKey, out var alreadyClosed))
        {
            QueueControl(writer, alreadyClosed.ToFrame(), "duplicate terminal close");
            return;
        }

        if (_closingSessions.ContainsKey(sessionKey))
        {
            return;
        }

        while (_sessions.TryGetValue(close.SessionId, out var candidate))
        {
            if (!candidate.Owns(presenceFence) || candidate.Generation != close.Generation)
            {
                ThrowIfPresenceFenceSuperseded(presenceFence);
                throw new RpcException(new Status(StatusCode.Aborted, "Terminal gateway close references an unknown or fenced session."));
            }

            if (!TryMoveActiveSessionToClosing(candidate))
            {
                continue;
            }

            QueueCleanup(candidate, NormalizeCloseReason(close.Reason), reportClosed: true);
            return;
        }

        if (_closingSessions.ContainsKey(sessionKey))
        {
            return;
        }

        // The previous Close may have reached this agent while its final
        // acknowledgement was lost with the stream. Reply idempotently, using
        // the writer that is attached now rather than the one from creation.
        QueueControl(writer, ClosedFrame(close.SessionId, close.Generation, NormalizeCloseReason(close.Reason)), "duplicate terminal close");
    }

    internal void HandleExited(GatewayTerminalSession session, string? reason)
    {
        if (!TryMoveActiveSessionToClosing(session))
        {
            return;
        }

        QueueCleanup(session, NormalizeCloseReason(reason ?? "shell_exited"), reportClosed: !session.IsFenced);
    }

    internal bool TryPreparePresenceFence(GatewayPresenceFence presenceFence, CancellationToken cancellationToken)
    {
        var stale = new List<GatewayTerminalSession>();
        lock (_presenceSync)
        {
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            if (_activePresenceFence is { } active && active == presenceFence)
            {
                _activePresenceOwnerToken = cancellationToken;
                PurgeExpiredPendingClosed();
                return true;
            }

            if (_activePresenceFence is { } current &&
                (presenceFence.TenantId != current.TenantId || presenceFence.AgentId != current.AgentId ||
                 (!_activePresenceOwnerToken.IsCancellationRequested && !presenceFence.CanSupersede(current))))
            {
                return false;
            }

            // The server's presence epoch is scoped to its actor lifetime and
            // can restart at one after an API restart. A cancelled parent has
            // relinquished ownership; its authenticated successor may replace
            // the old fence even when the numeric epoch has not increased.
            _activePresenceFence = presenceFence;
            _activePresenceOwnerToken = cancellationToken;
            _currentWriter = null;
            foreach (var session in _sessions.Values.Where(candidate => !candidate.Owns(presenceFence)).ToArray())
            {
                if (!TryRemoveActiveSession(session))
                {
                    continue;
                }

                session.Fence();
                stale.Add(session);
            }

            foreach (var closing in _closingSessions.Values.Where(candidate => !candidate.Owns(presenceFence)))
            {
                // A Close/exit worker may still be unwinding when the parent
                // presence fence changes. It may finish local cleanup, but it
                // must never announce that old ephemeral PTY to the new owner.
                closing.Fence();
            }

            foreach (var pending in _pendingClosed)
            {
                if (pending.Key.PresenceFence != presenceFence)
                {
                    _pendingClosed.TryRemove(pending.Key, out _);
                }
            }
        }

        foreach (var session in stale)
        {
            _log($"Terminal gateway fenced stale PTY session={session.SessionId} generation={session.Generation} for a new presence fence.");
            QueueCleanup(session, "terminal_presence_fenced", reportClosed: false);
        }

        PurgeExpiredPendingClosed();
        return true;
    }

    internal bool TryAddActiveSession(GatewayTerminalSession session)
    {
        var key = new TerminalSessionKey(session.SessionId, session.Generation, session.PresenceFence);
        lock (_presenceSync)
        {
            return _activePresenceFence is { } active && active == session.PresenceFence &&
                   !_closingSessions.ContainsKey(key) &&
                   _sessions.TryAdd(session.SessionId, session);
        }
    }

    private bool TryRemoveActiveSession(GatewayTerminalSession session) =>
        TryRemoveExact(_sessions, session.SessionId, session);

    private bool TryMoveActiveSessionToClosing(GatewayTerminalSession session)
    {
        var key = new TerminalSessionKey(session.SessionId, session.Generation, session.PresenceFence);
        lock (_presenceSync)
        {
            if (!TryRemoveExact(_sessions, session.SessionId, session))
            {
                return false;
            }

            // Session admission takes this same lock and checks this exact
            // key, so a replayed Start cannot slip between active removal and
            // the closing lifecycle reservation.
            _closingSessions.TryAdd(key, session);
            return true;
        }
    }

    private bool IsCurrentSession(GatewayTerminalSession session) =>
        _sessions.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, session);

    private void AttachOwnedSessions(GatewayPresenceFence presenceFence, GatewayWriter writer)
    {
        foreach (var session in _sessions.Values.Where(candidate => candidate.Owns(presenceFence)))
        {
            session.Attach(writer);
        }
    }

    private void DetachOwnedSessions(GatewayPresenceFence presenceFence, GatewayWriter writer)
    {
        foreach (var session in _sessions.Values.Where(candidate => candidate.Owns(presenceFence)))
        {
            session.Detach(writer);
        }
    }

    private void AttachCurrentWriter(GatewayPresenceFence presenceFence, GatewayWriter writer)
    {
        lock (_presenceSync)
        {
            if (_activePresenceFence is { } active && active == presenceFence)
            {
                _currentWriter = writer;
            }
        }
    }

    private void DetachCurrentWriter(GatewayPresenceFence presenceFence, GatewayWriter writer)
    {
        lock (_presenceSync)
        {
            if (_activePresenceFence is { } active && active == presenceFence && ReferenceEquals(_currentWriter, writer))
            {
                _currentWriter = null;
            }
        }
    }

    private bool TryQueueCurrentControl(GatewayPresenceFence presenceFence, AgentTerminalFrame frame)
    {
        lock (_presenceSync)
        {
            return _activePresenceFence is { } active && active == presenceFence &&
                   _currentWriter?.TryQueueControl(frame) == true;
        }
    }

    internal bool IsActivePresenceFence(GatewayPresenceFence presenceFence)
    {
        lock (_presenceSync)
        {
            return _activePresenceFence is { } active && active == presenceFence;
        }
    }

    private void ThrowIfPresenceFenceSuperseded(GatewayPresenceFence presenceFence)
    {
        if (!IsActivePresenceFence(presenceFence))
        {
            throw new PresenceFenceSupersededException();
        }
    }

    internal void QueueCleanup(GatewayTerminalSession session, string reason, bool reportClosed)
    {
        if (!session.TryBeginCleanup())
        {
            return;
        }

        var work = new CleanupWork(session, NormalizeCloseReason(reason), reportClosed);
        if (!_pendingCleanup.TryAdd(session, work))
        {
            return;
        }

        if (TryScheduleCleanup(work))
        {
            return;
        }

        var deferred = Interlocked.Increment(ref _deferredCleanupCount);
        if (deferred == 1 || deferred % OutputDropLogInterval == 0)
        {
            _log($"Terminal gateway cleanup queue is saturated; deferring local teardown session={session.SessionId} generation={session.Generation} count={deferred}.");
        }
    }

    private bool TryScheduleCleanup(CleanupWork work)
    {
        if (!work.TryMarkScheduled())
        {
            return true;
        }

        if (_cleanupQueue.Writer.TryWrite(work))
        {
            return true;
        }

        work.MarkUnscheduled();
        return false;
    }

    private void ScheduleDeferredCleanup()
    {
        foreach (var work in _pendingCleanup.Values)
        {
            if (!TryScheduleCleanup(work))
            {
                return;
            }
        }
    }

    private async Task ProcessCleanupQueueAsync()
    {
        try
        {
            await foreach (var work in _cleanupQueue.Reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))
            {
                if (!_pendingCleanup.TryRemove(work.Session, out var pending) || !ReferenceEquals(pending, work))
                {
                    ScheduleDeferredCleanup();
                    continue;
                }

                try
                {
                    await CleanupAsync(pending).ConfigureAwait(false);
                }
                finally
                {
                    ScheduleDeferredCleanup();
                }
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task CleanupAsync(CleanupWork work)
    {
        var reportClosed = work.ReportClosed;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            timeout.CancelAfter(CleanupTimeout);
            await work.Session.Host.RequestCloseAsync(work.Reason, timeout.Token)
                .WaitAsync(CleanupTimeout, _disposeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            reportClosed = false;
            return;
        }
        catch (OperationCanceledException)
        {
            _log($"Terminal gateway cleanup timed out session={work.Session.SessionId} generation={work.Session.Generation}.");
        }
        catch (TimeoutException)
        {
            _log($"Terminal gateway cleanup timed out session={work.Session.SessionId} generation={work.Session.Generation}.");
        }
        catch (Exception exception)
        {
            _log($"Terminal gateway cleanup failed session={work.Session.SessionId} generation={work.Session.Generation}: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            CompleteCleanup(work, reportClosed);
        }
    }

    private void CompleteCleanup(CleanupWork work, bool reportClosed)
    {
        var key = new TerminalSessionKey(work.Session.SessionId, work.Session.Generation, work.Session.PresenceFence);
        if (reportClosed && !work.Session.IsFenced)
        {
            var pending = new PendingClosedTerminal(key, work.Reason, DateTimeOffset.UtcNow);
            _pendingClosed[key] = pending;
            TryQueueCurrentControl(work.Session.PresenceFence, pending.ToFrame());
            PurgeExpiredPendingClosed();
            _log($"client.terminal.cleanup.completed session={work.Session.SessionId} generation={work.Session.Generation}.");
        }

        work.Session.Dispose();
        TryRemoveExact(_closingSessions, key, work.Session);
    }

    private void PurgeExpiredPendingClosed()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pending in _pendingClosed)
        {
            if (now - pending.Value.CreatedAtUtc > PendingClosedLifetime)
            {
                _pendingClosed.TryRemove(pending.Key, out _);
            }
        }

        var excess = _pendingClosed.Count - MaximumPendingClosedFrames;
        if (excess <= 0)
        {
            return;
        }

        foreach (var pending in _pendingClosed.Values.OrderBy(candidate => candidate.CreatedAtUtc).Take(excess))
        {
            _pendingClosed.TryRemove(pending.Key, out _);
        }
    }

    private void ValidateAccepted(GatewayTerminalFrame frame, GatewayPresenceSession session)
    {
        ValidateFrame(frame, session);
        if (!GatewayAuthority.IsAkka(frame.Accepted.TerminalAuthority))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Terminal gateway did not admit the expected authority."));
        }
    }

    private void ValidateFrame(GatewayTerminalFrame frame, GatewayPresenceSession session)
    {
        if (!string.Equals(frame.ProtocolVersion, _options.ProtocolVersion, StringComparison.Ordinal) || frame.TenantId != session.TenantId ||
            !string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != session.ConnectionEpoch || !string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(StatusCode.DataLoss, "Terminal gateway returned a frame for another presence session."));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_presenceSync)
        {
            _currentWriter = null;
            _activePresenceFence = null;
        }

        foreach (var session in _sessions.Values.ToArray())
        {
            if (TryRemoveActiveSession(session))
            {
                session.Fence();
                session.TryBeginCleanup();
                session.Dispose();
            }
        }

        foreach (var session in _closingSessions.Values.ToArray())
        {
            session.Fence();
            session.Dispose();
        }

        foreach (var work in _pendingCleanup.Values)
        {
            work.Session.Fence();
            work.Session.Dispose();
        }

        _pendingCleanup.Clear();
        _cleanupQueue.Writer.TryComplete();
        _disposeCts.Cancel();
    }

    private static void QueueControl(GatewayWriter writer, AgentTerminalFrame frame, string operation)
    {
        if (!writer.TryQueueControl(frame))
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"Terminal gateway {operation} could not be queued."));
        }
    }

    private static AgentTerminalFrame OpenedFrame(string sessionId, ulong generation) =>
        new() { Opened = new TerminalSessionOpened { SessionId = sessionId, Generation = generation } };

    private static AgentTerminalFrame ClosedFrame(string sessionId, ulong generation, string reason) =>
        new()
        {
            Closed = new TerminalSessionClosed
            {
                SessionId = sessionId,
                Generation = generation,
                Reason = Trim(reason, 128)
            }
        };

    private static AgentTerminalFrame FailedFrame(string sessionId, ulong generation, string code, string message) =>
        new()
        {
            Failed = new TerminalSessionFailed
            {
                SessionId = sessionId,
                Generation = generation,
                Code = Trim(code, 64),
                Message = Trim(message, 512)
            }
        };

    private static AgentTerminalFrame ResizeAppliedFrame(TerminalResize resize, TerminalResizeResult result) =>
        new()
        {
            ResizeApplied = new TerminalResizeApplied
            {
                SessionId = resize.SessionId,
                Generation = resize.Generation,
                SessionSequence = resize.SessionSequence,
                Result = result.Result,
                RequestedColumns = checked((uint)result.RequestedCols),
                RequestedRows = checked((uint)result.RequestedRows),
                AppliedColumns = checked((uint)Math.Max(result.AppliedCols.GetValueOrDefault(), 0)),
                AppliedRows = checked((uint)Math.Max(result.AppliedRows.GetValueOrDefault(), 0)),
                Error = result.Error ?? string.Empty
            }
        };

    private static string NormalizeCloseReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "terminal_closed" : Trim(reason, 128);

    private static string Trim(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static bool TryRemoveExact<TKey, TValue>(ConcurrentDictionary<TKey, TValue> dictionary, TKey key, TValue value)
        where TKey : notnull =>
        ((ICollection<KeyValuePair<TKey, TValue>>)dictionary).Remove(new KeyValuePair<TKey, TValue>(key, value));

    private async Task ObserveReconciliationAsync(Task reconciliation, CancellationToken cancellationToken)
    {
        try
        {
            await reconciliation.ConfigureAwait(false);
        }
        catch (PresenceFenceSupersededException)
        {
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _log($"Terminal gateway reconciliation ended unexpectedly: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task ObserveWriterStopAsync(GatewayWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            await writer.StopAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _log($"Terminal gateway writer stopped with an expected stream transition: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private readonly record struct TerminalSessionKey(string SessionId, ulong Generation, GatewayPresenceFence PresenceFence);

    private sealed class CleanupWork
    {
        private int _scheduled;

        public CleanupWork(GatewayTerminalSession session, string reason, bool reportClosed)
        {
            Session = session;
            Reason = reason;
            ReportClosed = reportClosed;
        }

        public GatewayTerminalSession Session { get; }
        public string Reason { get; }
        public bool ReportClosed { get; }

        public bool TryMarkScheduled() => Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0;

        public void MarkUnscheduled() => Volatile.Write(ref _scheduled, 0);
    }

    private sealed class PresenceFenceSupersededException : OperationCanceledException
    {
        public PresenceFenceSupersededException()
            : base("Terminal gateway presence fence was superseded.")
        {
        }
    }

    private sealed record PendingClosedTerminal(TerminalSessionKey Key, string Reason, DateTimeOffset CreatedAtUtc)
    {
        public AgentTerminalFrame ToFrame() => ClosedFrame(Key.SessionId, Key.Generation, Reason);
    }

    internal readonly record struct GatewayPresenceFence(int TenantId, Guid AgentId, ulong ConnectionEpoch, Guid ConnectionId)
    {
        public static GatewayPresenceFence From(GatewayPresenceSession session) =>
            new(session.TenantId, session.AgentId, session.ConnectionEpoch, session.ConnectionId);

        public bool CanSupersede(GatewayPresenceFence current) =>
            TenantId == current.TenantId && AgentId == current.AgentId && ConnectionEpoch > current.ConnectionEpoch;
    }

    /// <summary>
    /// Serializes physical gRPC writes while keeping control/lifecycle frames
    /// ahead of lossy PTY output. Callers never wait for output capacity.
    /// </summary>
    internal sealed class GatewayWriter : IDisposable
    {
        private const int ControlCapacity = 128;
        private const int OutputCapacity = 64;
        private readonly IClientStreamWriter<AgentTerminalFrame> _stream;
        private readonly GatewayPresenceSession _presence;
        private readonly string _protocolVersion;
        private readonly TimeSpan _writeTimeout;
        private sealed record ControlWrite(AgentTerminalFrame Frame, Action? Written);
        private readonly Channel<ControlWrite> _control = Channel.CreateBounded<ControlWrite>(
            new BoundedChannelOptions(ControlCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = true
            });
        private readonly Channel<AgentTerminalFrame> _output = Channel.CreateBounded<AgentTerminalFrame>(
            new BoundedChannelOptions(OutputCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = true
            });
        private Task? _pump;
        private ulong _sequence;
        private int _started;
        private int _stopped;

        internal GatewayWriter(
            IClientStreamWriter<AgentTerminalFrame> stream,
            GatewayPresenceSession presence,
            string protocolVersion,
            TimeSpan? writeTimeout = null)
        {
            _stream = stream;
            _presence = presence;
            _protocolVersion = protocolVersion;
            _writeTimeout = writeTimeout ?? FrameWriteTimeout;
        }

        public Task Completion => _pump ?? Task.CompletedTask;

        public Task WriteHelloAsync(AgentTerminalFrame frame, CancellationToken cancellationToken) =>
            WriteFrameAsync(frame, cancellationToken, isHello: true);

        public void Start(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                throw new InvalidOperationException("Terminal gateway writer was started more than once.");
            }

            _pump = PumpAsync(cancellationToken);
        }

        public bool TryQueueControl(AgentTerminalFrame frame, Action? written = null) =>
            Volatile.Read(ref _started) != 0 && Volatile.Read(ref _stopped) == 0 && _control.Writer.TryWrite(new(frame, written));

        public ValueTask QueueControlAsync(AgentTerminalFrame frame, CancellationToken cancellationToken, Action? written = null)
        {
            if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _stopped) != 0)
            {
                return ValueTask.FromException(new ChannelClosedException("Terminal gateway writer is unavailable."));
            }

            return _control.Writer.WriteAsync(new(frame, written), cancellationToken);
        }

        public bool TryQueueOutput(string sessionId, ulong generation, ulong sessionSequence, string content) =>
            Volatile.Read(ref _started) != 0 && Volatile.Read(ref _stopped) == 0 &&
            _output.Writer.TryWrite(new AgentTerminalFrame
            {
                Output = new TerminalOutput
                {
                    SessionId = sessionId,
                    Generation = generation,
                    SessionSequence = sessionSequence,
                    Content = ByteString.CopyFromUtf8(content)
                }
            });

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _control.Writer.TryComplete();
                _output.Writer.TryComplete();
            }

            if (_pump is { } pump)
            {
                await pump.ConfigureAwait(false);
            }
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    while (_control.Reader.TryRead(out var control))
                    {
                        await WriteFrameAsync(control.Frame, cancellationToken, isHello: false).ConfigureAwait(false);
                        control.Written?.Invoke();
                    }

                    if (_output.Reader.TryRead(out var output))
                    {
                        await WriteFrameAsync(output, cancellationToken, isHello: false).ConfigureAwait(false);
                        continue;
                    }

                    using var wakeup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var controlReady = _control.Reader.WaitToReadAsync(wakeup.Token).AsTask();
                    var outputReady = _output.Reader.WaitToReadAsync(wakeup.Token).AsTask();
                    await Task.WhenAny(controlReady, outputReady).ConfigureAwait(false);
                    wakeup.Cancel();
                    await ObserveCanceledWaitersAsync(controlReady, outputReady, wakeup.Token).ConfigureAwait(false);

                    if (controlReady.IsCompletedSuccessfully && !controlReady.Result &&
                        outputReady.IsCompletedSuccessfully && !outputReady.Result)
                    {
                        return;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _stopped, 1);
                _control.Writer.TryComplete();
                _output.Writer.TryComplete();
            }
        }

        private async Task WriteFrameAsync(AgentTerminalFrame frame, CancellationToken cancellationToken, bool isHello)
        {
            frame.ProtocolVersion = _protocolVersion;
            frame.TenantId = _presence.TenantId;
            frame.ClientId = _presence.AgentId.ToString("D");
            frame.ConnectionEpoch = _presence.ConnectionEpoch;
            frame.ConnectionId = _presence.ConnectionId.ToString("D");
            frame.Sequence = isHello ? 0 : checked(++_sequence);

            var write = _stream.WriteAsync(frame);
            try
            {
                await write.WaitAsync(_writeTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                ObserveFaultedWrite(write);
                throw new IOException("Terminal gateway frame write timed out.");
            }
        }

        private static void ObserveFaultedWrite(Task write)
        {
            _ = write.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static async Task ObserveCanceledWaitersAsync(Task<bool> controlReady, Task<bool> outputReady, CancellationToken cancellationToken)
        {
            try
            {
                await Task.WhenAll(controlReady, outputReady).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        public void Dispose()
        {
            _control.Writer.TryComplete();
            _output.Writer.TryComplete();
        }
    }

    internal sealed class GatewayTerminalSession : IDisposable
    {
        private readonly object _writerLock = new();
        private readonly Action<string> _log;
        private GatewayWriter? _writer;
        private GatewayWriter? _openedWriter;
        private const int PendingOutputFrameLimit = 64;
        private const int OutputFrameByteLimit = 16 * 1024;
        private const int PendingOutputByteLimit = PendingOutputFrameLimit * OutputFrameByteLimit;
        private readonly Queue<(ulong Sequence, string Content, int Bytes)> _pendingOutput = new();
        private int _pendingOutputBytes;
        private long _outputSequence;
        private long _droppedOutputFrames;
        private int _started;
        private int _fenced;
        private int _closing;
        private int _disposed;

        internal GatewayTerminalSession(
            string sessionId,
            ulong generation,
            GatewayPresenceFence presenceFence,
            ITerminalHostSession host,
            Action<string> log)
        {
            SessionId = sessionId;
            Generation = generation;
            PresenceFence = presenceFence;
            Host = host;
            _log = log;
        }

        public string SessionId { get; }
        public ulong Generation { get; }
        public GatewayPresenceFence PresenceFence { get; }
        public ITerminalHostSession Host { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public bool IsStarted => Volatile.Read(ref _started) != 0;
        public bool IsFenced => Volatile.Read(ref _fenced) != 0;
        public bool IsClosing => Volatile.Read(ref _closing) != 0;
        public long DroppedOutputFrames => Interlocked.Read(ref _droppedOutputFrames);

        public bool Owns(GatewayPresenceFence presenceFence) => !IsFenced && PresenceFence == presenceFence;

        public void MarkStarted() => Interlocked.Exchange(ref _started, 1);

        public void Attach(GatewayWriter writer)
        {
            lock (_writerLock)
            {
                if (!IsFenced && !IsClosing && Volatile.Read(ref _disposed) == 0)
                {
                    if (!ReferenceEquals(_writer, writer))
                    {
                        _openedWriter = null;
                        DiscardPendingOutputLocked();
                    }
                    _writer = writer;
                }
            }
        }

        public bool IsAttached(GatewayWriter writer)
        {
            lock (_writerLock)
            {
                return ReferenceEquals(_writer, writer);
            }
        }

        public void Detach(GatewayWriter writer)
        {
            lock (_writerLock)
            {
                if (ReferenceEquals(_writer, writer))
                {
                    _writer = null;
                    _openedWriter = null;
                    DiscardPendingOutputLocked();
                }
            }
        }

        public void Fence()
        {
            Interlocked.Exchange(ref _fenced, 1);
            lock (_writerLock)
            {
                _writer = null;
                _openedWriter = null;
                DiscardPendingOutputLocked();
            }
        }

        public bool TryBeginCleanup()
        {
            if (Interlocked.CompareExchange(ref _closing, 1, 0) != 0)
            {
                return false;
            }

            lock (_writerLock)
            {
                _openedWriter = null;
                DiscardPendingOutputLocked();
            }
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            return true;
        }

        public Task WriteOutputAsync(string data, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(data) || cancellationToken.IsCancellationRequested) return Task.CompletedTask;
            lock (_writerLock)
            {
                if (IsFenced || IsClosing || Volatile.Read(ref _disposed) != 0 || Cancellation.IsCancellationRequested)
                    return Task.CompletedTask;
                // Serialize assignment with enqueueing: stdout/stderr callbacks may run concurrently.
                var sequence = checked((ulong)++_outputSequence);
                if (_writer is { } writer)
                {
                    if (ReferenceEquals(_openedWriter, writer))
                    {
                        if (writer.TryQueueOutput(SessionId, Generation, sequence, data)) return Task.CompletedTask;
                    }
                    else
                    {
                        var bytes = Encoding.UTF8.GetByteCount(data);
                        if (bytes <= OutputFrameByteLimit && _pendingOutput.Count < PendingOutputFrameLimit &&
                            bytes <= PendingOutputByteLimit - _pendingOutputBytes)
                        {
                            _pendingOutput.Enqueue((sequence, data, bytes));
                            _pendingOutputBytes += bytes;
                            return Task.CompletedTask;
                        }
                    }
                }
                RecordOutputDrop();
            }
            return Task.CompletedTask;
        }

        public bool TryQueueOpened()
        {
            lock (_writerLock)
            {
                if (IsFenced || IsClosing || Volatile.Read(ref _disposed) != 0 || _writer is not { } writer)
                    return false;
                return writer.TryQueueControl(OpenedFrame(SessionId, Generation), () => OpenedWritten(writer));
            }
        }

        public ValueTask QueueOpenedAsync(GatewayWriter writer, CancellationToken cancellationToken)
        {
            lock (_writerLock)
            {
                if (IsFenced || IsClosing || Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(_writer, writer))
                    return ValueTask.CompletedTask;
                return writer.QueueControlAsync(OpenedFrame(SessionId, Generation), cancellationToken, () => OpenedWritten(writer));
            }
        }

        private void OpenedWritten(GatewayWriter writer)
        {
            lock (_writerLock)
            {
                if (IsFenced || IsClosing || Volatile.Read(ref _disposed) != 0 || Cancellation.IsCancellationRequested ||
                    !ReferenceEquals(_writer, writer)) return;
                // The physical writer invokes this only after Opened has been sent.
                // Merely placing Opened in its separate control queue is not an ordering barrier.
                _openedWriter = writer;
                while (_pendingOutput.TryDequeue(out var pending))
                {
                    _pendingOutputBytes -= pending.Bytes;
                    if (!writer.TryQueueOutput(SessionId, Generation, pending.Sequence, pending.Content)) RecordOutputDrop();
                }
            }
        }

        private void DiscardPendingOutputLocked()
        {
            while (_pendingOutput.TryDequeue(out _)) RecordOutputDrop();
            _pendingOutputBytes = 0;
        }

        private void RecordOutputDrop()
        {
            var dropped = Interlocked.Increment(ref _droppedOutputFrames);
            if (dropped == 1 || dropped % OutputDropLogInterval == 0)
                _log($"Terminal gateway output dropped session={SessionId} generation={Generation} count={dropped}; output is lossy while the transport is unavailable, opening, or pressured.");
        }

        public bool TryQueueClosed(string reason) => TryQueueControl(ClosedFrame(SessionId, Generation, reason));

        public bool TryQueueResizeApplied(TerminalResize resize, TerminalResizeResult result) =>
            TryQueueControl(ResizeAppliedFrame(resize, result));

        private bool TryQueueControl(AgentTerminalFrame frame)
        {
            if (IsFenced || Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            lock (_writerLock)
            {
                return _writer?.TryQueueControl(frame) == true;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (_writerLock)
            {
                _openedWriter = null;
                DiscardPendingOutputLocked();
            }
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            finally
            {
                Host.Dispose();
                Cancellation.Dispose();
            }
        }
    }
}

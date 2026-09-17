using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Observability;
using NetRatel.API.Realtime.Shadow;
using NetRatel.API.Services.Terminal;
using NetRatel.Application.Fanout;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Terminals;

namespace NetRatel.API.Gateway;

/// <summary>Bounded, transient terminal transport registry. No terminal bytes are persisted.</summary>
public interface IAgentTerminalSessionRegistry
{
    AgentTerminalGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, IReadOnlyList<string> availableShells, IReadOnlyList<string>? capabilities = null);

    AgentTerminalGatewayRegistration RegisterProvisional(ClientKey client, Guid connectionId, ulong connectionEpoch, IReadOnlyList<string> availableShells, IReadOnlyList<string>? capabilities = null) =>
        Register(client, connectionId, connectionEpoch, availableShells, capabilities);
    GatewayTerminalAvailability? GetAvailability(ClientKey client);
    Task<GatewayTerminalSession> OpenAsync(ClientKey client, string shellType, string? workingDirectory, int columns, int rows, CancellationToken ct);
    Task SendInputAsync(string sessionId, ulong generation, ReadOnlyMemory<byte> content, CancellationToken ct);
    Task ResizeAsync(string sessionId, ulong generation, int columns, int rows, CancellationToken ct);
    Task CloseAsync(string sessionId, ulong generation, string reason, CancellationToken ct);
    GatewayTerminalOutputSubscription Subscribe(string sessionId, ulong generation);
    GatewayTerminalSession? Get(string sessionId);
    bool TryReceiveOpened(ClientKey client, TerminalSessionOpened opened);
    Task<bool> TryReceiveOutputAsync(ClientKey client, TerminalOutput output, CancellationToken ct);
    bool TryReceiveResizeApplied(ClientKey client, TerminalResizeApplied resize);
    bool TryReceiveClosed(ClientKey client, TerminalSessionClosed closed);
    bool TryReceiveFailed(ClientKey client, TerminalSessionFailed failed);
}

/// <summary>Bounded, transient replay for sequential MCP output reads; browser subscriptions remain live-only.</summary>
public interface IAgentTerminalOutputReplayRegistry
{
    Task<GatewayTerminalOutputWindow> ReadOutputWindowAsync(string sessionId, ulong generation, ulong afterSequence,
        int maximumRecords, int maximumBytes, TimeSpan wait, CancellationToken cancellationToken);
}

public sealed record GatewayTerminalOutputRecord(ulong Sequence, ReadOnlyMemory<byte> Content);
public sealed record GatewayTerminalOutputWindow(IReadOnlyList<GatewayTerminalOutputRecord> Records,
    ulong NextSequence, bool HasMore, bool Gap, bool Completed);

/// <summary>
/// Additive recovery capability for the terminal registry. Keeping it separate
/// from the transport interface preserves existing gateway consumers while the
/// API learns how to adopt only durable, policy-frozen Production leases.
/// </summary>
public interface IAgentTerminalSessionRecoveryRegistry
{
    Task<bool> TryRecoverOpenedAsync(ClientKey client, TerminalSessionOpened opened, CancellationToken cancellationToken);
}

/// <summary>
/// Contains a valid reannouncement for a PTY that the server deliberately does
/// not own. The close is emitted only on the exact current presence transport;
/// it never creates or adopts session state.
/// </summary>
public interface IAgentTerminalSessionRejectionRegistry
{
    Task<bool> TryRejectOpenedAsync(
        ClientKey client,
        Guid connectionId,
        ulong connectionEpoch,
        TerminalSessionOpened opened,
        CancellationToken cancellationToken);
}

/// <summary>
/// Binds inbound lifecycle frames to the exact gRPC registration that
/// delivered them. A same-fence replacement must not let an old stream mutate
/// a session after the new registration becomes authoritative.
/// </summary>
public interface IAgentTerminalRegistrationBoundRegistry
{
    bool IsCurrentRegistration(ClientKey client, Guid registrationId);
    bool TryReceiveOpened(ClientKey client, Guid registrationId, TerminalSessionOpened opened);
    Task<bool> TryRecoverOpenedAsync(ClientKey client, Guid registrationId, TerminalSessionOpened opened, CancellationToken cancellationToken);
    Task<bool> TryReceiveOutputAsync(ClientKey client, Guid registrationId, TerminalOutput output, CancellationToken cancellationToken);
    bool TryReceiveResizeApplied(ClientKey client, Guid registrationId, TerminalResizeApplied resize);
    bool TryReceiveClosed(ClientKey client, Guid registrationId, TerminalSessionClosed closed);
    bool TryReceiveFailed(ClientKey client, Guid registrationId, TerminalSessionFailed failed);
    Task<bool> TryRejectOpenedAsync(ClientKey client, Guid registrationId, Guid connectionId, ulong connectionEpoch, TerminalSessionOpened opened, CancellationToken cancellationToken);
}

/// <summary>
/// Optional recovery admission fence for a registration-bound terminal
/// registry. The gateway invokes the validator after durable recovery I/O but
/// before the recovered PTY can be installed locally, so a parent-presence
/// change during that lookup cannot revive a stale stream.
/// </summary>
public interface IAgentTerminalRegistrationBoundRecoveryAdmissionRegistry : IAgentTerminalRegistrationBoundRegistry
{
    Task<bool> TryRecoverOpenedAsync(
        ClientKey client,
        Guid registrationId,
        TerminalSessionOpened opened,
        Func<CancellationToken, Task> ensureStillAdmittedAsync,
        CancellationToken cancellationToken);
}

/// <summary>
/// Additive fixed-ID open capability used by the Production terminal lease
/// adapter. A confirmation consumes one idempotency key into one durable
/// session ID before a PTY start frame is ever queued.
/// </summary>
public interface IAgentTerminalSessionLeaseRegistry
{
    Task<GatewayTerminalSession> OpenWithSessionIdAsync(
        ClientKey client,
        string sessionId,
        ulong generation,
        string shellType,
        string? workingDirectory,
        int columns,
        int rows,
        CancellationToken cancellationToken);
}

public sealed record GatewayTerminalAvailability(
    Guid ConnectionId,
    ulong ConnectionEpoch,
    IReadOnlyList<string> AvailableShells,
    DateTimeOffset RegisteredAtUtc,
    bool SupportsIdempotentClose,
    Guid? RegistrationId = null);

public sealed record GatewayTerminalSession(
    string SessionId,
    int TenantId,
    Guid AgentId,
    ulong Generation,
    string ShellType,
    int Columns,
    int Rows,
    string State,
    DateTimeOffset CreatedAtUtc,
    string Authority,
    string? FailureCode = null,
    string? FailureMessage = null,
    long DroppedOutputFrames = 0);

public class TerminalGatewayActionException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class AgentTerminalSessionUnavailableException(ClientKey client)
    : TerminalGatewayActionException("terminal_transport_unavailable", $"No active terminal gateway transport exists for tenant {client.TenantId}, agent {client.AgentId:D}.");

public sealed class AgentTerminalSessionRegistry(
    TimeProvider timeProvider,
    IShadowFanoutSink fanout,
    TerminalTimingRecorder? timing = null,
    IServiceScopeFactory? scopeFactory = null,
    ILogger<AgentTerminalSessionRegistry>? logger = null)
    : IAgentTerminalSessionRegistry, IAgentTerminalOutputReplayRegistry, IAgentTerminalSessionRecoveryRegistry, IAgentTerminalSessionRejectionRegistry, IAgentTerminalSessionLeaseRegistry, IAgentTerminalRegistrationBoundRecoveryAdmissionRegistry
{
    public const string IdempotentCloseCapability = "idempotent-close";
    private const int FrameLimit = 16 * 1024;
    private const int OutputSubscriberLimit = 4;
    private static readonly TimeSpan OpeningTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OverallOpeningTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TerminalTombstoneLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TransportReconnectGrace = TimeSpan.FromSeconds(45);
    private readonly object _transportSync = new();
    private readonly ConcurrentDictionary<ClientKey, AgentTerminalTransport> _agents = new();
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new(StringComparer.Ordinal);

    public AgentTerminalGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, IReadOnlyList<string> availableShells, IReadOnlyList<string>? capabilities = null) =>
        RegisterCore(client, connectionId, connectionEpoch, availableShells, capabilities, provisional: false);

    public AgentTerminalGatewayRegistration RegisterProvisional(ClientKey client, Guid connectionId, ulong connectionEpoch, IReadOnlyList<string> availableShells, IReadOnlyList<string>? capabilities = null) =>
        RegisterCore(client, connectionId, connectionEpoch, availableShells, capabilities, provisional: true);

    private AgentTerminalGatewayRegistration RegisterCore(ClientKey client, Guid connectionId, ulong connectionEpoch, IReadOnlyList<string> availableShells, IReadOnlyList<string>? capabilities, bool provisional)
    {
        var transport = new AgentTerminalTransport(client, connectionId, connectionEpoch, NormalizeShells(availableShells), SupportsIdempotentClose(capabilities), timeProvider.GetUtcNow());
        AgentTerminalTransport? previous = null;
        var sameFenceReplacement = false;
        lock (_transportSync)
        {
            if (_agents.TryGetValue(client, out previous))
            {
                if (!GatewaySessionRegistrationFence.CanReplace(connectionId, connectionEpoch, previous.ConnectionId, previous.ConnectionEpoch))
                {
                    transport.Complete();
                    throw new AgentGatewayRegistrationFencedException();
                }

                sameFenceReplacement = previous.Matches(connectionId, connectionEpoch);
                if (sameFenceReplacement)
                {
                    // State changes before availability is removed/replaced. A
                    // concurrent input route can consequently observe only a
                    // reconnecting session, never opened-without-transport.
                    SuspendForTransportLocked(previous);
                }
            }

            if (!sameFenceReplacement)
            {
                // The prior gRPC registration may already have disappeared
                // before a new parent presence fence arrives. Fence every
                // locally retained session outside this exact connection, not
                // merely the sessions of a still-present old transport.
                // Otherwise a Suspended A session can be revived by B's
                // Opened frame and later reject input as presence-fenced.
                FailSessionsOutsidePresenceFenceLocked(
                    client,
                    connectionId,
                    connectionEpoch,
                    "terminal_presence_fence_replaced",
                    "The parent authenticated presence fence was replaced.");
            }

            transport.IsAdmitted = !provisional;
            _agents[client] = transport;
            // Complete the old transport while the registration gate is still
            // held. A caller that selected it before this handoff can no
            // longer enqueue a frame after the replacement becomes current.
            previous?.Complete();
        }

        NetRatelAkkaTelemetry.TerminalTransportRegistered();
        if (sameFenceReplacement)
        {
            NetRatelAkkaTelemetry.TerminalTransportReconnected();
        }

        if (previous is not null)
        {
            logger?.LogInformation(
                "api.terminal.transport.registration.replaced tenantId={TenantId} agentId={AgentId} connectionId={ConnectionId} connectionEpoch={ConnectionEpoch} registrationId={RegistrationId} samePresenceFence={SamePresenceFence}",
                client.TenantId,
                client.AgentId,
                connectionId,
                connectionEpoch,
                transport.RegistrationId,
                sameFenceReplacement);
        }

        UpdateTransportMetrics();
        if (!provisional) ResumeForTransport(transport);

        return new(
            transport.Reader,
            () =>
        {
            var removed = false;
            lock (_transportSync)
            {
                if (_agents.TryGetValue(client, out var current) && ReferenceEquals(current, transport))
                {
                    // Suspend while this registration is still current so no
                    // caller can observe a live session after availability
                    // disappears. Late disposal of a replaced registration is
                    // intentionally a no-op.
                    SuspendForTransportLocked(transport);
                    transport.Complete();
                    _agents.TryRemove(client, out _);
                    removed = true;
                }
            }

            if (removed)
            {
                UpdateTransportMetrics();
            }
        },
            transport.MarkWritten,
            () => IsCurrentTransport(transport),
            transport.CompletionToken,
            transport.RegistrationId,
            () =>
            {
                lock (_transportSync)
                {
                    if (!IsCurrentTransportLocked(transport)) return false;
                    transport.IsAdmitted = true;
                }
                ResumeForTransport(transport);
                return IsCurrentTransport(transport);
            });
    }

    public GatewayTerminalAvailability? GetAvailability(ClientKey client)
    {
        lock (_transportSync)
        {
            return _agents.TryGetValue(client, out var transport) && transport.IsAdmitted
                ? new GatewayTerminalAvailability(transport.ConnectionId, transport.ConnectionEpoch, transport.AvailableShells, transport.RegisteredAtUtc, transport.SupportsIdempotentClose, transport.RegistrationId)
                : null;
        }
    }

    public Task<GatewayTerminalSession> OpenAsync(ClientKey client, string shellType, string? workingDirectory, int columns, int rows, CancellationToken ct) =>
        OpenCoreAsync(client, Guid.NewGuid().ToString("N"), 1, shellType, workingDirectory, columns, rows, ct);

    public Task<GatewayTerminalSession> OpenWithSessionIdAsync(
        ClientKey client,
        string sessionId,
        ulong generation,
        string shellType,
        string? workingDirectory,
        int columns,
        int rows,
        CancellationToken cancellationToken)
    {
        if (sessionId is not { Length: 32 } || !sessionId.All(char.IsAsciiHexDigit) || generation == 0)
            throw new ArgumentException("A fixed terminal open requires a canonical session ID and positive generation.");
        return OpenCoreAsync(client, sessionId, generation, shellType, workingDirectory, columns, rows, cancellationToken);
    }

    private async Task<GatewayTerminalSession> OpenCoreAsync(
        ClientKey client,
        string sessionId,
        ulong generation,
        string shellType,
        string? workingDirectory,
        int columns,
        int rows,
        CancellationToken ct)
    {
        AgentTerminalTransport transport;
        TerminalSession session;
        var shell = NormalizeShell(shellType) ?? throw new TerminalGatewayActionException("terminal_not_supported", $"Unsupported shell '{shellType}'.");
        lock (_transportSync)
        {
            transport = _agents.TryGetValue(client, out var current) && current.IsAdmitted
                ? current
                : throw new AgentTerminalSessionUnavailableException(client);
            if (!transport.AvailableShells.Contains(shell, StringComparer.OrdinalIgnoreCase))
            {
                throw new TerminalGatewayActionException("terminal_shell_unavailable", $"Shell '{shell}' is not available on this agent.");
            }

            session = new TerminalSession(
                sessionId,
                client,
                transport.ConnectionId,
                transport.ConnectionEpoch,
                shell,
                workingDirectory,
                columns,
                rows,
                transport.SupportsIdempotentClose,
                timeProvider.GetUtcNow(),
                generation);
            if (!_sessions.TryAdd(session.Id, session)) throw new TerminalGatewayActionException("terminal_session_collision", "The terminal session identifier is already active.");
        }

        try
        {
            session.BeginOverallOpeningDeadline(
                OverallOpeningTimeout,
                timeProvider,
                () => HandleOpeningTimeout(session, dispatchAttempt: null));
            await DispatchStartAsync(session, transport, ct).ConfigureAwait(false);
            logger?.LogInformation(
                "api.terminal.open.created tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation} connectionId={ConnectionId} connectionEpoch={ConnectionEpoch}",
                client.TenantId,
                client.AgentId,
                session.Id,
                session.Generation,
                transport.ConnectionId,
                transport.ConnectionEpoch);
            NetRatelAkkaTelemetry.RecordAuthorityRequest("terminal", "akka", fallbackUsed: false, "dev");
            NetRatelAkkaTelemetry.RecordAuthorityEvent("terminal", "akka", fallbackUsed: false, "dev");
            NetRatelAkkaTelemetry.SetTerminalActiveSessions(ActiveSessionCount());
            return session.Snapshot();
        }
        catch (TerminalGatewayActionException exception) when (exception.Code is "terminal_transport_reconnecting" or "terminal_transport_backpressured")
        {
            // The immutable Start stays on the session. A lost transport
            // replays it on the next matching registration; a saturated but
            // healthy writer retries it without tearing down that transport.
            return session.Snapshot();
        }
        catch
        {
            _sessions.TryRemove(session.Id, out _);
            session.Dispose();
            NetRatelAkkaTelemetry.RecordAuthorityFailure("terminal", "akka", fallbackUsed: false, "dev");
            throw;
        }
    }

    public Task SendInputAsync(string sessionId, ulong generation, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        if (content.Length is 0 or > FrameLimit) throw new ArgumentException("Terminal input frame is empty or exceeds the maximum frame size.");
        ct.ThrowIfCancellationRequested();
        TerminalSession session;
        AgentTerminalTransport transport;
        TerminalTransportEnqueueResult enqueue;
        lock (_transportSync)
        {
            (session, transport) = ResolveInputDispatchLocked(sessionId, generation);
            enqueue = transport.TryInput(session.Id, session.Generation, session.NextBrowserSequence(), content);
        }

        if (enqueue == TerminalTransportEnqueueResult.Closed)
        {
            RemoveTransport(transport);
            throw TransportReconnecting(session.Client);
        }

        if (enqueue == TerminalTransportEnqueueResult.Backpressured)
        {
            throw TransportBackpressured(session.Client);
        }

        timing?.RecordFrame(sessionId, TerminalTransportKind.AkkaGateway, "input", content.Length);
        timing?.RecordStage(sessionId, TerminalTransportKind.AkkaGateway, "api.input.dispatch", 0);
        return Task.CompletedTask;
    }

    public Task ResizeAsync(string sessionId, ulong generation, int columns, int rows, CancellationToken ct)
    {
        if (columns is < 1 or > 512 || rows is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(columns));
        ct.ThrowIfCancellationRequested();
        TerminalSession session;
        AgentTerminalTransport transport;
        ulong sequence;
        TerminalTransportEnqueueResult enqueue;
        lock (_transportSync)
        {
            (session, transport) = ResolveInputDispatchLocked(sessionId, generation);
            sequence = session.RequestResize(columns, rows);
            enqueue = transport.TryResize(session.Id, session.Generation, sequence, columns, rows);
        }

        if (enqueue == TerminalTransportEnqueueResult.Closed)
        {
            session.FailPendingResize("terminal_transport_reconnecting", "The terminal transport closed before the resize could be dispatched.");
            RemoveTransport(transport);
            throw TransportReconnecting(session.Client);
        }

        if (enqueue == TerminalTransportEnqueueResult.Backpressured)
        {
            session.FailPendingResize("terminal_transport_backpressured", "The terminal transport is temporarily busy. Retry the resize shortly.");
            throw TransportBackpressured(session.Client);
        }

        return session.WaitForResizeAppliedAsync(sequence, ct);
    }

    public async Task CloseAsync(string sessionId, ulong generation, string reason, CancellationToken ct)
    {
        var session = GetSession(sessionId, generation);
        var beganClosing = session.TryBeginClosing(reason);
        if (!beganClosing)
        {
            if (session.IsClosing && !session.SupportsIdempotentClose)
            {
                // Older agents do not negotiate duplicate-close acknowledgement.
                // Preserve their historic terminal failure path instead of
                // retaining a closing session that cannot be safely retried.
                Fail(session, "terminal_close_retry_unsupported", "The connected terminal client does not support a safe close retry.");
            }

            if (!session.IsClosing || !session.SupportsIdempotentClose)
            {
                return;
            }
        }

        // A close can race a transient terminal-stream disconnect. Its exact
        // reason was installed atomically with the Closing transition above,
        // so a reannouncement can never observe Closing and enqueue a default
        // cleanup reason before the operator's decision is retained.
        if (beganClosing)
        {
            // Operator-initiated closes need the same bounded tombstone as a
            // timed-out Start. Otherwise a disconnected client could leave a
            // session in Closing forever after its acknowledgement is lost.
            _ = ExpirePendingCloseAsync(
                session,
                "terminal_close_timeout",
                "The terminal client did not acknowledge the requested close before reconciliation expired.");
            _ = PersistPendingCloseSafelyAsync(session);
        }

        await DispatchPendingCloseAsync(session, ct).ConfigureAwait(false);
    }

    public GatewayTerminalOutputSubscription Subscribe(string sessionId, ulong generation) => GetSession(sessionId, generation).Subscribe();

    public async Task<GatewayTerminalOutputWindow> ReadOutputWindowAsync(string sessionId, ulong generation, ulong afterSequence,
        int maximumRecords, int maximumBytes, TimeSpan wait, CancellationToken cancellationToken)
    {
        if (maximumRecords is < 1 or > 100 || maximumBytes <= 0 || wait < TimeSpan.Zero || wait > TimeSpan.FromSeconds(15))
            throw new TerminalGatewayActionException("terminal_stream_invalid", "The terminal output window bounds are invalid.");
        var session = GetSession(sessionId, generation);
        using var timeout = new CancellationTokenSource(wait, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (snapshot, changed) = session.ReadOutput(afterSequence, maximumRecords, maximumBytes);
            if (snapshot.Records.Count > 0 || snapshot.Completed || timeout.IsCancellationRequested) return snapshot;
            try { await changed.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Take a final snapshot: output may have arrived at the deadline.
                return session.ReadOutput(afterSequence, maximumRecords, maximumBytes).Window;
            }
        }
    }

    public GatewayTerminalSession? Get(string sessionId) => _sessions.TryGetValue(sessionId, out var session) ? session.Snapshot() : null;

    public bool TryReceiveOpened(ClientKey client, TerminalSessionOpened opened)
    {
        if (!TryGet(opened.SessionId, client, opened.Generation, out var session)) return false;
        if (session.IsClosing)
        {
            if (!session.SupportsIdempotentClose)
            {
                // A close that may already have reached an older client cannot
                // be replayed safely after the stream moves. Fail the local
                // tombstone deterministically instead of sending duplicate
                // close decisions on a capability downgrade.
                Fail(session, "terminal_close_retry_unsupported", "The connected terminal client does not support a safe close retry after transport loss.");
                return true;
            }

            // The client retains PTYs during a transient stream reconnect. A
            // closing session must therefore admit its reannouncement so the
            // pending close can be delivered, without reopening it for input.
            _ = DispatchPendingCloseSafelyAsync(session);
            return true;
        }

        var wasSuspended = session.IsSuspended;
        var transition = session.TryOpen();
        if (transition == TerminalOpenTransition.Rejected) return false;
        if (transition == TerminalOpenTransition.AlreadyOpened) return true;

        NetRatelAkkaTelemetry.TerminalSessionOpened();
        logger?.LogInformation(
            wasSuspended ? "api.terminal.transport.resumed tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation}" :
                "api.terminal.opened.received tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation}",
            client.TenantId,
            client.AgentId,
            session.Id,
            session.Generation);
        Publish(session, ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active);
        return true;
    }

    public bool IsCurrentRegistration(ClientKey client, Guid registrationId)
    {
        lock (_transportSync)
        {
            return _agents.TryGetValue(client, out var transport) && transport.RegistrationId == registrationId;
        }
    }

    public bool TryReceiveOpened(ClientKey client, Guid registrationId, TerminalSessionOpened opened)
    {
        lock (_transportSync)
        {
            if (!_agents.TryGetValue(client, out var transport) || transport.RegistrationId != registrationId)
            {
                return false;
            }

            // Bound lifecycle frames are meaningful only for the exact parent
            // presence fence that created the transient session. A durable
            // recovery below constructs a fresh session with this transport's
            // fence, but an old suspended in-memory PTY must stay fenced.
            if (TryGetKnown(opened.SessionId, client, opened.Generation, out var existing) &&
                !existing.ConnectionMatches(transport.ConnectionId, transport.ConnectionEpoch))
            {
                return false;
            }

            return TryReceiveOpened(client, opened);
        }
    }

    /// <summary>
    /// Rehydrates only a persisted Production lease after an authenticated
    /// agent reannounces its existing PTY. Unknown sessions stay rejected;
    /// expiry is converted to a close-pending lease before any local routing
    /// state is created.
    /// </summary>
    public Task<bool> TryRecoverOpenedAsync(ClientKey client, TerminalSessionOpened opened, CancellationToken cancellationToken) =>
        TryRecoverOpenedCoreAsync(client, registrationId: null, opened, ensureStillAdmittedAsync: null, cancellationToken);

    public Task<bool> TryRecoverOpenedAsync(ClientKey client, Guid registrationId, TerminalSessionOpened opened, CancellationToken cancellationToken) =>
        TryRecoverOpenedCoreAsync(client, registrationId, opened, ensureStillAdmittedAsync: null, cancellationToken);

    public Task<bool> TryRecoverOpenedAsync(
        ClientKey client,
        Guid registrationId,
        TerminalSessionOpened opened,
        Func<CancellationToken, Task> ensureStillAdmittedAsync,
        CancellationToken cancellationToken) =>
        TryRecoverOpenedCoreAsync(client, registrationId, opened, ensureStillAdmittedAsync, cancellationToken);

    private async Task<bool> TryRecoverOpenedCoreAsync(
        ClientKey client,
        Guid? registrationId,
        TerminalSessionOpened opened,
        Func<CancellationToken, Task>? ensureStillAdmittedAsync,
        CancellationToken cancellationToken)
    {
        var received = registrationId is { } boundRegistrationId
            ? TryReceiveOpened(client, boundRegistrationId, opened)
            : TryReceiveOpened(client, opened);
        if (received)
            return true;
        if (scopeFactory is null || string.IsNullOrWhiteSpace(opened.SessionId) || opened.Generation == 0)
            return false;

        await using var scope = scopeFactory.CreateAsyncScope();
        var recovery = scope.ServiceProvider.GetService<IMcpOperatorTerminalSessionRecovery>();
        if (recovery is null)
            return false;

        var persisted = await recovery.TryRecoverAsync(
            client.TenantId,
            client.AgentId,
            opened.SessionId,
            opened.Generation,
            cancellationToken).ConfigureAwait(false);
        if (persisted is null)
            return false;

        // Durable recovery can take arbitrarily longer than the inbound
        // frame's first presence check. Revalidate immediately before local
        // admission so a parent presence fence that changed during the lookup
        // cannot rehydrate a PTY for the stale stream.
        if (ensureStillAdmittedAsync is not null)
        {
            await ensureStillAdmittedAsync(cancellationToken).ConfigureAwait(false);
        }

        TerminalSession? adopted = null;
        AgentTerminalTransport? admittedTransport = null;
        var closePending = false;
        lock (_transportSync)
        {
            // This admission check deliberately occurs after the asynchronous
            // durable lookup and while the session is installed. An old gRPC
            // stream can never use a replacement registration to adopt its
            // reannouncement after a same-fence handoff.
            if (!_agents.TryGetValue(client, out var transport) ||
                registrationId is { } expectedRegistrationId && transport.RegistrationId != expectedRegistrationId ||
                !transport.SupportsIdempotentClose)
            {
                return false;
            }

            admittedTransport = transport;

            if (_sessions.ContainsKey(persisted.SessionId))
            {
                // A concurrent local session (including a tombstone) retains
                // ownership. Re-run its normal, registration-bound transition
                // outside this admission path rather than replacing it.
                adopted = null;
            }
            else
            {
                var session = new TerminalSession(
                    persisted.SessionId,
                    client,
                    transport.ConnectionId,
                    transport.ConnectionEpoch,
                    shellType: persisted.ShellType,
                    workingDirectory: null,
                    columns: persisted.Columns,
                    rows: persisted.Rows,
                    supportsIdempotentClose: true,
                    created: persisted.CreatedAtUtc,
                    generation: persisted.Generation);
                if (persisted.CloseRequested)
                {
                    session.TryBeginClosing(persisted.CloseReason ?? "terminal_policy_lease_expired");
                    closePending = true;
                }
                else if (session.TryOpen() == TerminalOpenTransition.Rejected)
                {
                    session.Dispose();
                    return false;
                }

                // Initialize the private lifecycle before publishing the
                // session into the concurrent dictionary. A caller can close
                // immediately after TryAdd, so publishing Requested and then
                // transitioning it here could otherwise remove a valid
                // ClosePending tombstone.
                if (_sessions.TryAdd(session.Id, session))
                {
                    adopted = session;
                }
                else
                {
                    session.Dispose();
                    closePending = false;
                }
            }
        }

        if (adopted is null)
        {
            return registrationId is { } currentRegistrationId
                ? TryReceiveOpened(client, currentRegistrationId, opened)
                : TryReceiveOpened(client, opened);
        }

        if (closePending)
        {
            // This deadline is deliberately armed before the post-admission
            // registration check below. A same-fence replacement can win
            // between adoption and the first Close replay, but it must not
            // leave the recovered tombstone Closing forever if no client ACK
            // ever arrives.
            _ = ExpirePendingCloseAsync(
                adopted,
                "terminal_policy_lease_expired",
                "The recovered terminal lease did not acknowledge its required close before reconciliation expired.");
        }

        if (closePending)
        {
            lock (_transportSync)
            {
                if (!IsCurrentTransportLocked(admittedTransport!))
                {
                    return false;
                }

                // Once TryAdd succeeded, the admitted registration owns this
                // reannouncement. A local close/fail may win before this
                // post-admission observation, but returning false would make
                // the service send terminal_session_rejected ahead of the
                // exact pending Close that owns the tombstone.
                if (!adopted.IsClosing)
                {
                    return true;
                }

                Publish(adopted, ShadowFanoutEventType.Updated, ShadowFanoutStatus.Pending);
            }
            await DispatchPendingCloseAsync(adopted, CancellationToken.None).ConfigureAwait(false);
            return true;
        }

        lock (_transportSync)
        {
            if (!IsCurrentTransportLocked(admittedTransport!))
            {
                return false;
            }

            // A close/fail that wins after successful admission is still
            // locally owned by this registration. Treat the Opened as
            // accepted so the caller cannot convert it into a stale-PTY
            // rejection close with a competing reason.
            if (!adopted.Opened)
            {
                return true;
            }

            NetRatelAkkaTelemetry.TerminalSessionOpened();
            NetRatelAkkaTelemetry.SetTerminalActiveSessions(ActiveSessionCount());
            Publish(adopted, ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active);
        }
        return true;
    }

    public Task<bool> TryRejectOpenedAsync(
        ClientKey client,
        Guid connectionId,
        ulong connectionEpoch,
        TerminalSessionOpened opened,
        CancellationToken cancellationToken) =>
        TryRejectOpenedCoreAsync(client, registrationId: null, connectionId, connectionEpoch, opened, cancellationToken);

    public Task<bool> TryRejectOpenedAsync(
        ClientKey client,
        Guid registrationId,
        Guid connectionId,
        ulong connectionEpoch,
        TerminalSessionOpened opened,
        CancellationToken cancellationToken) =>
        TryRejectOpenedCoreAsync(client, registrationId, connectionId, connectionEpoch, opened, cancellationToken);

    private async Task<bool> TryRejectOpenedCoreAsync(
        ClientKey client,
        Guid? registrationId,
        Guid connectionId,
        ulong connectionEpoch,
        TerminalSessionOpened opened,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            TerminalRejectionEnqueueAttempt attempt;
            AgentTerminalTransport? transport;
            lock (_transportSync)
            {
                transport = string.IsNullOrWhiteSpace(opened.SessionId) || opened.SessionId.Length > 128 || opened.Generation == 0 ||
                            !_agents.TryGetValue(client, out var current) ||
                            registrationId is { } expectedRegistrationId && current.RegistrationId != expectedRegistrationId ||
                            !current.Matches(connectionId, connectionEpoch)
                    ? null
                    : current;
                if (transport is null)
                {
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                attempt = transport.TryReject(opened.SessionId, opened.Generation);
                if (attempt.Outcome == TerminalTransportEnqueueResult.Accepted)
                {
                    NetRatelAkkaTelemetry.TerminalStalePtyRejected();
                    return true;
                }
            }

            if (attempt.Outcome == TerminalTransportEnqueueResult.Closed)
            {
                RemoveTransport(transport!);
                return false;
            }

            if (attempt.Progress is null)
            {
                return false;
            }

            // The gRPC writer runs independently of this inbound reader. Hold
            // only this unowned reannouncement until a bounded retry slot
            // opens, rather than dropping it, spinning, or growing memory.
            await attempt.Progress.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<bool> TryReceiveOutputAsync(ClientKey client, TerminalOutput output, CancellationToken ct)
    {
        if (output.Content.Length > FrameLimit || !TryGet(output.SessionId, client, output.Generation, out var session)) return Task.FromResult(false);
        // Browser output is lossy under pressure by design. It must never hold
        // the sole agent inbound loop hostage and thereby prevent Opened,
        // Closed, Failed, or resize lifecycle frames from being processed.
        var published = session.TryPublishOutput(output.SessionSequence, output.Content.Memory, out var admitted);
        if (!admitted) return Task.FromResult(false);
        if (!published)
        {
            // Every bounded browser reader was full. Replay retains the
            // admitted frame independently, without extending browser queues.
            // Backpressure is still protocol-successful: bytes were
            // intentionally dropped rather than allowed to block the agent.
            return Task.FromResult(!session.IsTerminal);
        }

        NetRatelAkkaTelemetry.TerminalFrameObserved();
        timing?.RecordFrame(output.SessionId, TerminalTransportKind.AkkaGateway, "output", output.Content.Length);
        timing?.RecordStage(output.SessionId, TerminalTransportKind.AkkaGateway, "agent.output.api", 0);
        Publish(session, ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, output.SessionSequence);
        return Task.FromResult(true);
    }

    public Task<bool> TryReceiveOutputAsync(ClientKey client, Guid registrationId, TerminalOutput output, CancellationToken cancellationToken)
    {
        lock (_transportSync)
        {
            return _agents.TryGetValue(client, out var transport) && transport.RegistrationId == registrationId
                ? TryReceiveOutputAsync(client, output, cancellationToken)
                : Task.FromResult(false);
        }
    }

    public bool TryReceiveResizeApplied(ClientKey client, TerminalResizeApplied resize)
    {
        if (!TryGet(resize.SessionId, client, resize.Generation, out var session)) return false;
        if (!session.TryApplyResize(resize)) return false;
        if (session.IsTerminal)
        {
            // A concurrent lifecycle terminal transition won after the
            // resize was applied; avoid publishing a later Active snapshot.
            return true;
        }

        timing?.RecordStage(resize.SessionId, TerminalTransportKind.AkkaGateway, "agent.resize.applied", 0);
        Publish(session, ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, resize.SessionSequence);
        return true;
    }

    public bool TryReceiveResizeApplied(ClientKey client, Guid registrationId, TerminalResizeApplied resize)
    {
        lock (_transportSync)
        {
            return _agents.TryGetValue(client, out var transport) && transport.RegistrationId == registrationId &&
                   TryReceiveResizeApplied(client, resize);
        }
    }

    public bool TryReceiveClosed(ClientKey client, TerminalSessionClosed closed)
    {
        if (!TryGetKnown(closed.SessionId, client, closed.Generation, out var session)) return false;
        return Close(session, closed.Reason) is not TerminalCloseTransition.Rejected;
    }

    public bool TryReceiveClosed(ClientKey client, Guid registrationId, TerminalSessionClosed closed)
    {
        lock (_transportSync)
        {
            return _agents.TryGetValue(client, out var transport) && transport.RegistrationId == registrationId &&
                   TryReceiveClosed(client, closed);
        }
    }

    public bool TryReceiveFailed(ClientKey client, TerminalSessionFailed failed)
    {
        if (!TryGet(failed.SessionId, client, failed.Generation, out var session)) return false;
        return Fail(
            session,
            string.IsNullOrWhiteSpace(failed.Code) ? "terminal_agent_rejected" : failed.Code,
            failed.Message) is TerminalFailTransition.Failed;
    }

    public bool TryReceiveFailed(ClientKey client, Guid registrationId, TerminalSessionFailed failed)
    {
        lock (_transportSync)
        {
            return _agents.TryGetValue(client, out var transport) && transport.RegistrationId == registrationId &&
                   TryReceiveFailed(client, failed);
        }
    }

    private AgentTerminalTransport GetAgent(ClientKey client)
    {
        lock (_transportSync)
        {
            return _agents.TryGetValue(client, out var transport) && transport.IsAdmitted
                ? transport
                : throw new AgentTerminalSessionUnavailableException(client);
        }
    }

    private TerminalSession GetSession(string sessionId, ulong generation) =>
        _sessions.TryGetValue(sessionId, out var session) && session.Generation == generation
            ? session
            : throw new TerminalGatewayActionException("terminal_session_not_found", "The terminal session was not found or is fenced.");

    private (TerminalSession Session, AgentTerminalTransport Transport) ResolveInputDispatchLocked(string sessionId, ulong generation)
    {
        var session = GetSession(sessionId, generation);
        if (session.IsTerminal || session.IsClosing)
        {
            throw new TerminalGatewayActionException(
                session.IsFailed ? "terminal_session_failed" : "terminal_session_closed",
                "The terminal session is closed, closing, or failed.");
        }

        if (session.IsSuspended)
        {
            throw TransportReconnecting(session.Client);
        }

        if (!session.Opened)
        {
            throw new TerminalGatewayActionException("terminal_opening", "The terminal session has not opened yet.");
        }

        if (!_agents.TryGetValue(session.Client, out var transport) || !transport.IsAdmitted)
        {
            session.TrySuspend();
            throw TransportReconnecting(session.Client);
        }

        if (!transport.Matches(session.ConnectionId, session.ConnectionEpoch))
        {
            throw new TerminalGatewayActionException("terminal_presence_fenced", "The terminal session belongs to a superseded presence connection.");
        }

        return (session, transport);
    }

    private bool TryGet(string sessionId, ClientKey client, ulong generation, out TerminalSession session) =>
        _sessions.TryGetValue(sessionId, out session!) && session.Client == client && session.Generation == generation && !session.IsTerminal;

    private bool TryGetKnown(string sessionId, ClientKey client, ulong generation, out TerminalSession session) =>
        _sessions.TryGetValue(sessionId, out session!) && session.Client == client && session.Generation == generation;

    private TerminalCloseTransition Close(TerminalSession session, string reason)
    {
        var transition = session.TryClose(reason);
        if (transition is not TerminalCloseTransition.Closed)
        {
            return transition;
        }

        NetRatelAkkaTelemetry.TerminalSessionClosed();
        NetRatelAkkaTelemetry.SetTerminalActiveSessions(ActiveSessionCount());
        Publish(session, ShadowFanoutEventType.Completed, ShadowFanoutStatus.Completed);
        ScheduleRemoval(session);
        return transition;
    }

    private TerminalFailTransition Fail(TerminalSession session, string code, string? message)
    {
        var transition = session.TryFail(code, message);
        if (transition is not TerminalFailTransition.Failed)
        {
            return transition;
        }

        NetRatelAkkaTelemetry.RecordAuthorityFailure("terminal", "akka", fallbackUsed: false, "dev");
        NetRatelAkkaTelemetry.SetTerminalActiveSessions(ActiveSessionCount());
        Publish(session, ShadowFanoutEventType.Completed, ShadowFanoutStatus.Failed);
        ScheduleRemoval(session);
        return transition;
    }

    private void FailSessionsOutsidePresenceFenceLocked(
        ClientKey client,
        Guid connectionId,
        ulong connectionEpoch,
        string code,
        string message)
    {
        foreach (var session in _sessions.Values.Where(session =>
                     session.Client == client &&
                     !session.ConnectionMatches(connectionId, connectionEpoch)))
        {
            Fail(session, code, message);
        }
    }

    private void ResumeForTransport(AgentTerminalTransport transport)
    {
        lock (_transportSync)
        {
            if (!IsCurrentTransportLocked(transport) || !transport.IsAdmitted) return;
            foreach (var session in _sessions.Values.Where(session => session.Client == transport.Client && session.ConnectionMatches(transport.ConnectionId, transport.ConnectionEpoch)))
            {
                if (session.NeedsStartReplay)
                {
                    NetRatelAkkaTelemetry.TerminalStartReplayed();
                    logger?.LogInformation(
                        "api.terminal.start.replayed tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation} registrationId={RegistrationId}",
                        session.Client.TenantId,
                        session.Client.AgentId,
                        session.Id,
                        session.Generation,
                        transport.RegistrationId);
                    _ = ReplayStartAsync(session, transport);
                    continue;
                }

                if (session.IsClosing)
                {
                    if (!session.SupportsIdempotentClose || !transport.SupportsIdempotentClose)
                    {
                        Fail(session, "terminal_close_retry_unsupported", "The active terminal transport does not support a safe close retry after transport loss.");
                    }
                    else
                    {
                        _ = DispatchPendingCloseSafelyAsync(session);
                    }
                }
            }
        }
    }

    private void SuspendForTransportLocked(AgentTerminalTransport transport)
    {
        foreach (var session in _sessions.Values.Where(session => session.Client == transport.Client && session.ConnectionMatches(transport.ConnectionId, transport.ConnectionEpoch)))
        {
            if (!session.TrySuspend()) continue;
            logger?.LogInformation(
                "api.terminal.transport.suspended tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation} connectionId={ConnectionId} connectionEpoch={ConnectionEpoch} registrationId={RegistrationId}",
                session.Client.TenantId,
                session.Client.AgentId,
                session.Id,
                session.Generation,
                transport.ConnectionId,
                transport.ConnectionEpoch,
                transport.RegistrationId);
            Publish(session, ShadowFanoutEventType.Updated, ShadowFanoutStatus.Pending);
            _ = ExpireSuspendedSessionAsync(session, transport);
        }
    }

    private async Task ExpireSuspendedSessionAsync(TerminalSession session, AgentTerminalTransport transport)
    {
        await Task.Delay(TransportReconnectGrace, timeProvider, CancellationToken.None).ConfigureAwait(false);
        lock (_transportSync)
        {
            if (session.IsSuspended)
            {
                // A replacement registration alone is not a resume. The PTY
                // must reannounce and transition this exact session back to
                // Opened before the bounded grace ends; otherwise it is a
                // likely orphan and must enter deterministic cleanup.
                HandleOpeningTimeout(session, dispatchAttempt: null, "terminal_reconnect_timeout", "The terminal gateway did not reconnect before its grace period expired.");
            }
        }
    }

    private void ScheduleRemoval(TerminalSession session) => _ = Task.Run(async () =>
    {
        await Task.Delay(TerminalTombstoneLifetime, timeProvider, CancellationToken.None).ConfigureAwait(false);
        if (_sessions.TryGetValue(session.Id, out var current) && ReferenceEquals(current, session) && current.IsTerminal)
        {
            _sessions.TryRemove(session.Id, out _);
            current.Dispose();
        }
    });

    private Task DispatchStartAsync(TerminalSession session, AgentTerminalTransport transport, CancellationToken cancellationToken)
    {
        ulong dispatchAttempt;
        TerminalTransportEnqueueResult enqueue;
        cancellationToken.ThrowIfCancellationRequested();
        lock (_transportSync)
        {
            if (!_agents.TryGetValue(session.Client, out var current) || !ReferenceEquals(current, transport) || !transport.IsAdmitted)
            {
                if (session.TrySuspend())
                {
                    Publish(session, ShadowFanoutEventType.Updated, ShadowFanoutStatus.Pending);
                }

                throw TransportReconnecting(session.Client);
            }

            if (!session.TryBeginStartDispatch(transport.RegistrationId, out dispatchAttempt))
            {
                return Task.CompletedTask;
            }

            logger?.LogInformation(
                "api.terminal.start.queued tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation} registrationId={RegistrationId} dispatchAttempt={DispatchAttempt}",
                session.Client.TenantId,
                session.Client.AgentId,
                session.Id,
                session.Generation,
                transport.RegistrationId,
                dispatchAttempt);
            enqueue = transport.TryStart(
                session.Id,
                session.Generation,
                session.ShellType,
                session.WorkingDirectory,
                session.Columns,
                session.Rows,
                () =>
                {
                    logger?.LogInformation(
                        "api.terminal.start.written tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation} registrationId={RegistrationId} dispatchAttempt={DispatchAttempt}",
                        session.Client.TenantId,
                        session.Client.AgentId,
                        session.Id,
                        session.Generation,
                        transport.RegistrationId,
                        dispatchAttempt);
                    session.MarkStartWritten(
                        transport.RegistrationId,
                        dispatchAttempt,
                        OpeningTimeout,
                        timeProvider,
                        () => HandleOpeningTimeout(session, dispatchAttempt));
                });

            if (enqueue == TerminalTransportEnqueueResult.Accepted)
            {
                // Publish while the registration gate is held so a later
                // presence replacement cannot make a stale Pending event
                // arrive after its Suspended/Failed transition.
                Publish(session, ShadowFanoutEventType.Updated, ShadowFanoutStatus.Pending);
            }
            else if (enqueue == TerminalTransportEnqueueResult.Backpressured)
            {
                session.ReturnStartToRequested(transport.RegistrationId, dispatchAttempt);
                Publish(session, ShadowFanoutEventType.Updated, ShadowFanoutStatus.Pending);
            }
        }

        if (enqueue == TerminalTransportEnqueueResult.Closed)
        {
            RemoveTransport(transport);
            throw TransportReconnecting(session.Client);
        }

        if (enqueue == TerminalTransportEnqueueResult.Backpressured)
        {
            ScheduleStartRetry(session, transport);
            throw TransportBackpressured(session.Client);
        }

        return Task.CompletedTask;
    }

    private async Task ReplayStartAsync(TerminalSession session, AgentTerminalTransport transport)
    {
        try
        {
            await DispatchStartAsync(session, transport, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TerminalGatewayActionException)
        {
            // The registration lifecycle already published the reconnecting
            // state. A later same-fence registration will retry the immutable
            // Start; this background replay must not create an unobserved task
            // failure.
            logger?.LogDebug(
                "api.terminal.start.replay.deferred tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation}",
                session.Client.TenantId,
                session.Client.AgentId,
                session.Id,
                session.Generation);
        }
    }

    private void ScheduleStartRetry(TerminalSession session, AgentTerminalTransport transport)
    {
        if (!session.TryScheduleStartRetry())
        {
            return;
        }

        _ = RetryStartAfterBackpressureAsync(session, transport);
    }

    private async Task RetryStartAfterBackpressureAsync(TerminalSession session, AgentTerminalTransport transport)
    {
        try
        {
            // The control reserve normally makes this path exceptional. A
            // bounded retry retains the same immutable Start without treating
            // transient writer pressure as a broken gRPC transport.
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, CancellationToken.None).ConfigureAwait(false);
            if (session.NeedsStartDispatch && IsCurrentTransport(transport))
            {
                await DispatchStartAsync(session, transport, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (TerminalGatewayActionException exception) when (exception.Code is "terminal_transport_reconnecting" or "terminal_transport_backpressured")
        {
            logger?.LogDebug(
                "api.terminal.start.retry.deferred tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation} code={Code}",
                session.Client.TenantId,
                session.Client.AgentId,
                session.Id,
                session.Generation,
                exception.Code);
        }
        finally
        {
            session.CompleteStartRetry();
            if (session.NeedsStartDispatch && IsCurrentTransport(transport))
            {
                ScheduleStartRetry(session, transport);
            }
        }
    }

    private Task DispatchPendingCloseAsync(TerminalSession session, CancellationToken cancellationToken)
    {
        AgentTerminalTransport? transport;
        TerminalTransportEnqueueResult? enqueue = null;
        var capabilityDowngraded = false;
        cancellationToken.ThrowIfCancellationRequested();
        lock (_transportSync)
        {
            transport = _agents.TryGetValue(session.Client, out var current) && current.IsAdmitted &&
                        current.Matches(session.ConnectionId, session.ConnectionEpoch)
                ? current
                : null;

            if (transport is null || !session.IsClosing)
            {
                return Task.CompletedTask;
            }

            if (session.SupportsIdempotentClose && !transport.SupportsIdempotentClose)
            {
                capabilityDowngraded = true;
            }
            else
            {
                enqueue = transport.TryClose(
                    session.Id,
                    session.Generation,
                    session.PendingCloseReason ?? "terminal_closed");
            }
        }

        if (capabilityDowngraded)
        {
            Fail(session, "terminal_close_retry_unsupported", "The active terminal transport no longer supports a safe close retry.");
            return Task.CompletedTask;
        }

        if (enqueue == TerminalTransportEnqueueResult.Closed)
        {
            RemoveTransport(transport);
        }
        else if (enqueue == TerminalTransportEnqueueResult.Backpressured)
        {
            SchedulePendingCloseRetry(session);
        }

        return Task.CompletedTask;
    }

    private async Task DispatchPendingCloseSafelyAsync(TerminalSession session)
    {
        try
        {
            await DispatchPendingCloseAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // No caller owns background reconciliation cancellation.
            logger?.LogDebug(
                "api.terminal.cleanup.close.cancelled tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation}",
                session.Client.TenantId,
                session.Client.AgentId,
                session.Id,
                session.Generation);
        }
        catch (Exception)
        {
            // A malformed or otherwise unexpected Close cannot be allowed to
            // leave an invisible PTY in an eternal pending state. Retire the
            // local tombstone; the next registration still fences any stale
            // reannouncement with an exact rejection close.
            Fail(session, "terminal_cleanup_dispatch_failed", "The terminal cleanup close could not be dispatched.");
        }
    }

    private void SchedulePendingCloseRetry(TerminalSession session)
    {
        if (!session.TrySchedulePendingCloseRetry())
        {
            return;
        }

        _ = RetryPendingCloseAfterBackpressureAsync(session);
    }

    private async Task RetryPendingCloseAfterBackpressureAsync(TerminalSession session)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, CancellationToken.None).ConfigureAwait(false);
            if (session.IsClosing)
            {
                await DispatchPendingCloseAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or TerminalGatewayActionException)
        {
            logger?.LogDebug(
                exception,
                "api.terminal.cleanup.close.retry.deferred tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation}",
                session.Client.TenantId,
                session.Client.AgentId,
                session.Id,
                session.Generation);
        }
        finally
        {
            session.CompletePendingCloseRetry();
            if (session.IsClosing && IsCurrentTransportForSession(session))
            {
                SchedulePendingCloseRetry(session);
            }
        }
    }

    private void HandleOpeningTimeout(
        TerminalSession session,
        ulong? dispatchAttempt,
        string code = "terminal_open_timeout",
        string message = "The agent did not open the terminal within the allowed time.")
    {
        lock (_transportSync)
        {
            if (!session.TryBeginTimeoutClose(dispatchAttempt, code, message))
            {
                return;
            }

            // Make the ClosePending state observable before another
            // registration can suspend, resume, or fence this generation.
            NetRatelAkkaTelemetry.RecordAuthorityFailure("terminal", "akka", fallbackUsed: false, "dev");
            NetRatelAkkaTelemetry.TerminalOpeningTimedOut();
            NetRatelAkkaTelemetry.TerminalCompensatingCloseQueued();
            Publish(session, ShadowFanoutEventType.Updated, ShadowFanoutStatus.Pending);
        }

        logger?.LogWarning(
            "api.terminal.open.timeout tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation} dispatchAttempt={DispatchAttempt} code={Code}",
            session.Client.TenantId,
            session.Client.AgentId,
            session.Id,
            session.Generation,
            dispatchAttempt,
            code);
        logger?.LogInformation(
            "api.terminal.cleanup.close.queued tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation} reason={Reason}",
            session.Client.TenantId,
            session.Client.AgentId,
            session.Id,
            session.Generation,
            code);
        _ = PersistPendingCloseSafelyAsync(session);
        _ = DispatchPendingCloseSafelyAsync(session);
        _ = ExpirePendingCloseAsync(session, code, message);
    }

    private async Task ExpirePendingCloseAsync(TerminalSession session, string code, string message)
    {
        await Task.Delay(TerminalTombstoneLifetime, timeProvider, CancellationToken.None).ConfigureAwait(false);
        if (session.IsClosing)
        {
            Fail(session, code, message);
        }
    }

    private async Task PersistPendingCloseSafelyAsync(TerminalSession session)
    {
        if (scopeFactory is null)
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetService<IMcpOperatorTerminalSessionStore>();
            if (store is null)
            {
                return;
            }

            var reason = session.PendingCloseReason ?? "terminal_closed";
            if (reason.Length > 128)
            {
                reason = reason[..128];
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = "terminal_closed";
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await store.RequestCloseAsync(session.Id, reason, timeProvider.GetUtcNow(), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger?.LogWarning(
                "api.terminal.cleanup.close.persistence.timed-out tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation}",
                session.Client.TenantId,
                session.Client.AgentId,
                session.Id,
                session.Generation);
        }
        catch (Exception exception)
        {
            // Durable lease persistence is a reconciliation aid; the live
            // registry remains in ClosePending and will still deliver the
            // exact compensating close over a matching transport.
            logger?.LogWarning(
                exception,
                "api.terminal.cleanup.close.persistence.failed tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation}",
                session.Client.TenantId,
                session.Client.AgentId,
                session.Id,
                session.Generation);
        }
    }

    private void RemoveTransport(AgentTerminalTransport transport)
    {
        var removed = false;
        lock (_transportSync)
        {
            if (_agents.TryGetValue(transport.Client, out var current) && ReferenceEquals(current, transport))
            {
                SuspendForTransportLocked(transport);
                transport.Complete();
                _agents.TryRemove(transport.Client, out _);
                removed = true;
            }
        }

        if (!removed)
        {
            transport.Complete();
        }
        if (removed)
        {
            UpdateTransportMetrics();
        }
    }

    private bool IsCurrentTransport(AgentTerminalTransport transport)
    {
        lock (_transportSync)
        {
            return IsCurrentTransportLocked(transport);
        }
    }

    private bool IsCurrentTransportLocked(AgentTerminalTransport transport) =>
        _agents.TryGetValue(transport.Client, out var current) && ReferenceEquals(current, transport);

    private bool IsCurrentTransportForSession(TerminalSession session)
    {
        lock (_transportSync)
        {
            return _agents.TryGetValue(session.Client, out var transport) && transport.IsAdmitted &&
                   transport.Matches(session.ConnectionId, session.ConnectionEpoch);
        }
    }

    private static TerminalGatewayActionException TransportReconnecting(ClientKey client) =>
        new(
            "terminal_transport_reconnecting",
            $"The terminal transport for tenant {client.TenantId}, agent {client.AgentId:D} is reconnecting. Retry after the session resumes.");

    private static TerminalGatewayActionException TransportBackpressured(ClientKey client) =>
        new(
            "terminal_transport_backpressured",
            $"The terminal transport for tenant {client.TenantId}, agent {client.AgentId:D} is temporarily busy. Retry shortly.");

    private int ActiveSessionCount() => _sessions.Values.Count(session => !session.IsTerminal);

    private void UpdateTransportMetrics()
    {
        lock (_transportSync)
        {
            NetRatelAkkaTelemetry.SetTerminalActiveTransports(_agents.Count);
        }
    }

    private void UpdateTerminalSessionMetrics()
    {
        long active = 0;
        long opened = 0;
        long opening = 0;
        long suspended = 0;
        foreach (var session in _sessions.Values)
        {
            if (!session.IsTerminal)
            {
                active++;
            }

            if (session.Opened)
            {
                opened++;
            }
            else if (session.IsOpening)
            {
                opening++;
            }
            else if (session.IsSuspended)
            {
                suspended++;
            }
        }

        NetRatelAkkaTelemetry.SetTerminalActiveSessions(active);
        NetRatelAkkaTelemetry.SetTerminalLifecycleSessions(opened, opening, suspended);
    }

    private static IReadOnlyList<string> NormalizeShells(IEnumerable<string> shells) =>
        shells.Select(NormalizeShell).Where(shell => shell is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool SupportsIdempotentClose(IReadOnlyList<string>? capabilities) =>
        capabilities?.Contains(IdempotentCloseCapability, StringComparer.Ordinal) == true;

    private static string? NormalizeShell(string? shell) => shell?.Trim().ToLowerInvariant() switch
    {
        "pwsh" or "powershell" or "cmd" or "bash" or "sh" or "zsh" => shell.Trim().ToLowerInvariant(),
        _ => null
    };

    private void Publish(TerminalSession session, ShadowFanoutEventType eventType, ShadowFanoutStatus status, ulong? sequence = null)
    {
        fanout.TryEnqueueBestEffort(new ShadowFanoutEnvelope(
            ShadowFanoutEnvelope.CurrentSchemaVersion,
            ShadowFanoutCategory.Terminal,
            new ShadowFanoutTarget(session.Client.TenantId, ShadowFanoutTargetScope.Terminal, session.Client.AgentId.ToString("D"), session.Id),
            eventType,
            status,
            timeProvider.GetUtcNow(),
            sequence,
            Diagnostics: new ShadowFanoutDiagnosticSummary(ActiveCount: status == ShadowFanoutStatus.Active ? 1UL : 0UL),
            IsAuthoritative: true));
        if (sequence is null)
        {
            UpdateTerminalSessionMetrics();
        }
    }

    private sealed class TerminalSession(
        string id,
        ClientKey client,
        Guid connectionId,
        ulong connectionEpoch,
        string shellType,
        string? workingDirectory,
        int columns,
        int rows,
        bool supportsIdempotentClose,
        DateTimeOffset created,
        ulong generation = 1) : IDisposable
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, Channel<ReadOnlyMemory<byte>>> _outputSubscribers = [];
        private const int ReplayFrameLimit = 64;
        private const int ReplayByteLimit = ReplayFrameLimit * FrameLimit;
        private readonly Queue<GatewayTerminalOutputRecord> _outputHistory = new();
        private TaskCompletionSource _outputChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _outputHistoryBytes;
        private bool _outputCompleted;
        private readonly CancellationTokenSource _overallOpeningCts = new();
        private CancellationTokenSource? _dispatchOpeningCts;
        private ulong _browser;
        private ulong _agent;
        private ulong _latestResizeSequence;
        private ulong _appliedResizeSequence;
        private ulong _dispatchAttempt;
        private Guid _activeRegistrationId;
        private TaskCompletionSource<ulong>? _resizeApplied;
        private bool _startRetryScheduled;
        private bool _pendingCloseRetryScheduled;
        private int _columns = columns;
        private int _rows = rows;
        private long _droppedOutputFrames;
        private TerminalLifecycle _state = TerminalLifecycle.Requested;
        private TerminalLifecycle? _suspendedFrom;
        private string? _failureCode;
        private string? _failureMessage;
        private string? _pendingCloseReason;

        public string Id { get; } = id;
        public ClientKey Client { get; } = client;
        public Guid ConnectionId { get; } = connectionId;
        public ulong ConnectionEpoch { get; } = connectionEpoch;
        public string ShellType { get; } = shellType;
        public string? WorkingDirectory { get; } = workingDirectory;
        public int Columns { get { lock (_sync) return _columns; } }
        public int Rows { get { lock (_sync) return _rows; } }
        public ulong Generation { get; } = generation;
        public bool SupportsIdempotentClose { get; } = supportsIdempotentClose;
        public bool Opened { get { lock (_sync) return _state == TerminalLifecycle.Opened; } }
        public bool IsOpening { get { lock (_sync) return _state is TerminalLifecycle.Requested or TerminalLifecycle.Dispatching or TerminalLifecycle.Opening; } }
        public bool IsClosing { get { lock (_sync) return _state == TerminalLifecycle.Closing; } }
        public bool IsClosed { get { lock (_sync) return _state == TerminalLifecycle.Closed; } }
        public bool IsFailed { get { lock (_sync) return _state == TerminalLifecycle.Failed; } }
        public bool IsTerminal { get { lock (_sync) return _state is TerminalLifecycle.Closed or TerminalLifecycle.Failed; } }
        public bool IsSuspended { get { lock (_sync) return _state == TerminalLifecycle.Suspended; } }
        public bool NeedsStartReplay
        {
            get
            {
                lock (_sync)
                {
                    return _state == TerminalLifecycle.Suspended && _suspendedFrom is not TerminalLifecycle.Opened;
                }
            }
        }

        public bool NeedsStartDispatch
        {
            get
            {
                lock (_sync)
                {
                    return _state == TerminalLifecycle.Requested ||
                           _state == TerminalLifecycle.Suspended && _suspendedFrom is not TerminalLifecycle.Opened;
                }
            }
        }

        public string? PendingCloseReason { get { lock (_sync) return _pendingCloseReason; } }

        public void BeginOverallOpeningDeadline(TimeSpan duration, TimeProvider timeProvider, Action timeout)
        {
            var overallOpeningToken = _overallOpeningCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(duration, timeProvider, overallOpeningToken).ConfigureAwait(false);
                    timeout();
                }
                catch (OperationCanceledException) when (overallOpeningToken.IsCancellationRequested)
                {
                    // The session reached a terminal lifecycle state first.
                    return;
                }
            });
        }

        public bool TryBeginStartDispatch(Guid registrationId, out ulong dispatchAttempt)
        {
            lock (_sync)
            {
                if (_state is not TerminalLifecycle.Requested and not TerminalLifecycle.Suspended ||
                    _state == TerminalLifecycle.Suspended && _suspendedFrom == TerminalLifecycle.Opened)
                {
                    dispatchAttempt = 0;
                    return false;
                }

                CancelDispatchOpeningTimer();
                // Opening is entered before the Start frame can be observed by
                // the agent. The receipt callback below only arms the timeout;
                // it never changes lifecycle state after an Opened race.
                _state = TerminalLifecycle.Opening;
                _suspendedFrom = null;
                _activeRegistrationId = registrationId;
                dispatchAttempt = checked(++_dispatchAttempt);
                return true;
            }
        }

        public void ReturnStartToRequested(Guid registrationId, ulong dispatchAttempt)
        {
            lock (_sync)
            {
                if (_state == TerminalLifecycle.Opening && _activeRegistrationId == registrationId && _dispatchAttempt == dispatchAttempt)
                {
                    _state = TerminalLifecycle.Requested;
                    _activeRegistrationId = Guid.Empty;
                    CancelDispatchOpeningTimer();
                }
            }
        }

        public bool TryScheduleStartRetry()
        {
            lock (_sync)
            {
                if (_startRetryScheduled || _state is TerminalLifecycle.Closing or TerminalLifecycle.Closed or TerminalLifecycle.Failed)
                {
                    return false;
                }

                _startRetryScheduled = true;
                return true;
            }
        }

        public void CompleteStartRetry()
        {
            lock (_sync)
            {
                _startRetryScheduled = false;
            }
        }

        public bool TrySchedulePendingCloseRetry()
        {
            lock (_sync)
            {
                if (_pendingCloseRetryScheduled || _state != TerminalLifecycle.Closing || !SupportsIdempotentClose)
                {
                    return false;
                }

                _pendingCloseRetryScheduled = true;
                return true;
            }
        }

        public void CompletePendingCloseRetry()
        {
            lock (_sync)
            {
                _pendingCloseRetryScheduled = false;
            }
        }

        public void MarkStartWritten(Guid registrationId, ulong dispatchAttempt, TimeSpan duration, TimeProvider timeProvider, Action timeout)
        {
            lock (_sync)
            {
                if (_state != TerminalLifecycle.Opening || _activeRegistrationId != registrationId || _dispatchAttempt != dispatchAttempt)
                {
                    return;
                }

                _dispatchOpeningCts = new CancellationTokenSource();
                var openingCts = _dispatchOpeningCts;
                var openingToken = openingCts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(duration, timeProvider, openingToken).ConfigureAwait(false);
                        timeout();
                    }
                    catch (OperationCanceledException) when (openingToken.IsCancellationRequested)
                    {
                        // A matching Opened, suspend, or close transition won.
                        return;
                    }
                });
            }
        }

        public ulong NextBrowserSequence() => checked(Interlocked.Increment(ref _browser));

        public TerminalOpenTransition TryOpen()
        {
            lock (_sync)
            {
                if (_state == TerminalLifecycle.Opened) return TerminalOpenTransition.AlreadyOpened;
                if (_state is not TerminalLifecycle.Requested and not TerminalLifecycle.Dispatching and not TerminalLifecycle.Opening and not TerminalLifecycle.Suspended)
                {
                    return TerminalOpenTransition.Rejected;
                }

                _state = TerminalLifecycle.Opened;
                _suspendedFrom = null;
                CancelDispatchOpeningTimer();
                _overallOpeningCts.Cancel();
                return TerminalOpenTransition.Opened;
            }
        }

        public bool TryPublishOutput(ulong sequence, ReadOnlyMemory<byte> bytes, out bool admitted)
        {
            admitted = false;
            var dropped = 0;
            var accepted = false;
            lock (_sync)
            {
                if (_state != TerminalLifecycle.Opened || sequence <= _agent) return false;
                // Sequence admission, bounded history, and reader notification share one gate.
                // A reader either sees this frame or waits on the signal completed by it.
                admitted = true;
                _agent = sequence;
                var retained = new GatewayTerminalOutputRecord(sequence, bytes.ToArray());
                _outputHistory.Enqueue(retained);
                _outputHistoryBytes += bytes.Length;
                while (_outputHistory.Count > ReplayFrameLimit || _outputHistoryBytes > ReplayByteLimit)
                    _outputHistoryBytes -= _outputHistory.Dequeue().Content.Length;
                SignalOutputChanged();

                foreach (var subscriber in _outputSubscribers.Values)
                {
                    if (subscriber.Writer.TryWrite(bytes))
                    {
                        accepted = true;
                    }
                    else
                    {
                        dropped++;
                    }
                }
            }

            if (dropped > 0)
            {
                Interlocked.Add(ref _droppedOutputFrames, dropped);
                for (var index = 0; index < dropped; index++)
                {
                    NetRatelAkkaTelemetry.TerminalOutputDropped();
                }
            }

            // Browser delivery stays lossy and non-blocking. MCP readers use
            // the separate bounded replay history, even between tool calls.
            return accepted || dropped == 0;
        }

        public (GatewayTerminalOutputWindow Window, Task Changed) ReadOutput(ulong afterSequence, int maximumRecords, int maximumBytes)
        {
            lock (_sync)
            {
                if (afterSequence > _agent)
                    throw new TerminalGatewayActionException("terminal_output_cursor_invalid", "The cursor is ahead of this session's retained output. Omit afterSequence to restart a bounded read after API recovery.");
                var records = new List<GatewayTerminalOutputRecord>();
                var bytes = 0;
                var next = afterSequence;
                var gap = false;
                var more = false;
                foreach (var record in _outputHistory)
                {
                    if (record.Sequence <= afterSequence) continue;
                    if (records.Count >= maximumRecords || record.Content.Length > maximumBytes - bytes)
                    {
                        if (records.Count == 0)
                            throw new TerminalGatewayActionException("terminal_output_limit_exceeded", "A terminal output frame exceeds the frozen byte limit.");
                        more = true;
                        break;
                    }
                    gap |= record.Sequence - next > 1;
                    records.Add(record);
                    bytes += record.Content.Length;
                    next = record.Sequence;
                }
                return (new(records, next, more, gap, _outputCompleted), _outputChanged.Task);
            }
        }

        private void SignalOutputChanged()
        {
            var signal = _outputChanged;
            _outputChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.TrySetResult();
        }

        public ulong RequestResize(int columns, int rows)
        {
            lock (_sync)
            {
                _resizeApplied?.TrySetResult(_latestResizeSequence);
                _latestResizeSequence = checked(++_browser);
                _resizeApplied = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _latestResizeSequence;
            }
        }

        public Task WaitForResizeAppliedAsync(ulong sequence, CancellationToken ct)
        {
            Task task;
            lock (_sync)
            {
                task = _latestResizeSequence == sequence
                    ? _resizeApplied?.Task ?? Task.CompletedTask
                    : Task.CompletedTask;
            }

            return task.WaitAsync(ct);
        }

        public bool TryApplyResize(TerminalResizeApplied resize)
        {
            lock (_sync)
            {
                if (_state is not TerminalLifecycle.Opened and not TerminalLifecycle.Suspended || resize.SessionSequence == 0)
                {
                    return false;
                }

                if (resize.SessionSequence < _latestResizeSequence || resize.SessionSequence <= _appliedResizeSequence)
                {
                    return true;
                }

                _appliedResizeSequence = resize.SessionSequence;
                if (string.Equals(resize.Result, "applied", StringComparison.OrdinalIgnoreCase) &&
                    resize.AppliedColumns is > 0 and <= 512 && resize.AppliedRows is > 0 and <= 512)
                {
                    _columns = checked((int)resize.AppliedColumns);
                    _rows = checked((int)resize.AppliedRows);
                }
                else
                {
                    _failureMessage = string.IsNullOrWhiteSpace(resize.Error) ? "The remote PTY could not apply the requested resize." : resize.Error;
                }

                _resizeApplied?.TrySetResult(resize.SessionSequence);
                return true;
            }
        }

        public void FailPendingResize(string code, string message)
        {
            lock (_sync)
            {
                _resizeApplied?.TrySetException(new TerminalGatewayActionException(code, message));
                _resizeApplied = null;
            }
        }

        public bool TrySuspend()
        {
            lock (_sync)
            {
                if (_state is not (TerminalLifecycle.Requested or TerminalLifecycle.Dispatching or TerminalLifecycle.Opening or TerminalLifecycle.Opened))
                {
                    return false;
                }

                _suspendedFrom = _state;
                _state = TerminalLifecycle.Suspended;
                CancelDispatchOpeningTimer();
                FailPendingResizeLocked("terminal_transport_reconnecting", "The terminal transport is reconnecting before the resize was acknowledged.");
                return true;
            }
        }

        public bool TryBeginClosing(string reason)
        {
            lock (_sync)
            {
                if (_state is TerminalLifecycle.Closing or TerminalLifecycle.Closed or TerminalLifecycle.Failed) return false;
                _state = TerminalLifecycle.Closing;
                _suspendedFrom = null;
                _pendingCloseReason ??= reason;
                CancelDispatchOpeningTimer();
                FailPendingResizeLocked("terminal_session_closed", "The terminal session is closing before the resize was acknowledged.");
                return true;
            }
        }

        public bool TryBeginTimeoutClose(ulong? dispatchAttempt, string code, string message)
        {
            lock (_sync)
            {
                if (dispatchAttempt is { } expected && (_dispatchAttempt != expected || _state is not TerminalLifecycle.Dispatching and not TerminalLifecycle.Opening))
                {
                    return false;
                }

                if (dispatchAttempt is null && _state is not (TerminalLifecycle.Requested or TerminalLifecycle.Dispatching or TerminalLifecycle.Opening or TerminalLifecycle.Suspended))
                {
                    return false;
                }

                _state = TerminalLifecycle.Closing;
                _suspendedFrom = null;
                _failureCode = code;
                _failureMessage = message;
                _pendingCloseReason = code;
                CancelDispatchOpeningTimer();
                _overallOpeningCts.Cancel();
                FailPendingResizeLocked("terminal_session_closed", "The terminal session is closing before the resize was acknowledged.");
                return true;
            }
        }

        public TerminalCloseTransition TryClose(string reason)
        {
            lock (_sync)
            {
                if (_state == TerminalLifecycle.Closed)
                {
                    return TerminalCloseTransition.AlreadyClosed;
                }

                if (_state == TerminalLifecycle.Failed && !CanAcceptLateCloseAfterTimeout())
                {
                    return TerminalCloseTransition.Rejected;
                }

                _state = TerminalLifecycle.Closed;
                _failureCode = null;
                _failureMessage = reason;
                CancelDispatchOpeningTimer();
                _overallOpeningCts.Cancel();
                FailPendingResizeLocked("terminal_session_closed", "The terminal session closed before the resize was acknowledged.");
                CompleteOutputSubscribers();
                return TerminalCloseTransition.Closed;
            }
        }

        public TerminalFailTransition TryFail(string code, string? message)
        {
            lock (_sync)
            {
                if (_state == TerminalLifecycle.Closed)
                {
                    return TerminalFailTransition.Rejected;
                }

                if (_state == TerminalLifecycle.Failed)
                {
                    return TerminalFailTransition.AlreadyFailed;
                }

                _state = TerminalLifecycle.Failed;
                _failureCode = code;
                _failureMessage = message;
                CancelDispatchOpeningTimer();
                _overallOpeningCts.Cancel();
                FailPendingResizeLocked("terminal_session_failed", "The terminal session failed before the resize was acknowledged.");
                CompleteOutputSubscribers();
                return TerminalFailTransition.Failed;
            }
        }

        public bool ConnectionMatches(Guid expectedConnectionId, ulong expectedConnectionEpoch) => ConnectionId == expectedConnectionId && ConnectionEpoch == expectedConnectionEpoch;
        public GatewayTerminalOutputSubscription Subscribe()
        {
            var subscriberId = Guid.NewGuid();
            var subscriber = Channel.CreateBounded<ReadOnlyMemory<byte>>(
                new BoundedChannelOptions(64)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = true,
                    SingleReader = true
                });
            lock (_sync)
            {
                if (_state is TerminalLifecycle.Closed or TerminalLifecycle.Failed)
                {
                    subscriber.Writer.TryComplete();
                }
                else
                {
                    // An old circuit can remain detached briefly while a new
                    // one reattaches. Keep a small, deterministic fan-out
                    // bound rather than retaining an unbounded collection of
                    // dead SSE readers.
                    while (_outputSubscribers.Count >= OutputSubscriberLimit)
                    {
                        var evicted = _outputSubscribers.First();
                        _outputSubscribers.Remove(evicted.Key);
                        evicted.Value.Writer.TryComplete();
                    }

                    _outputSubscribers.Add(subscriberId, subscriber);
                }
            }

            return new(subscriber.Reader, () =>
            {
                lock (_sync)
                {
                    if (_outputSubscribers.Remove(subscriberId, out var current))
                    {
                        current.Writer.TryComplete();
                    }
                }
            });
        }
        public GatewayTerminalSession Snapshot()
        {
            lock (_sync)
            {
                var state = _state switch
                {
                    TerminalLifecycle.Requested => "requested",
                    TerminalLifecycle.Dispatching or TerminalLifecycle.Opening => "opening",
                    TerminalLifecycle.Opened => "opened",
                    TerminalLifecycle.Suspended => "suspended",
                    TerminalLifecycle.Closing => "closing",
                    TerminalLifecycle.Closed => "closed",
                    TerminalLifecycle.Failed => "failed",
                    _ => "failed"
                };
                return new GatewayTerminalSession(Id, Client.TenantId, Client.AgentId, Generation, ShellType, _columns, _rows, state, created, "akka", _failureCode, _failureMessage, Interlocked.Read(ref _droppedOutputFrames));
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                CancelDispatchOpeningTimer();
                _overallOpeningCts.Cancel();
                _overallOpeningCts.Dispose();
                FailPendingResizeLocked("terminal_session_closed", "The terminal session was removed before the resize was acknowledged.");
                CompleteOutputSubscribers();
            }
        }

        private void CancelDispatchOpeningTimer()
        {
            if (_dispatchOpeningCts is null)
            {
                return;
            }

            _dispatchOpeningCts.Cancel();
            _dispatchOpeningCts.Dispose();
            _dispatchOpeningCts = null;
        }

        private void CompleteOutputSubscribers()
        {
            _outputCompleted = true;
            SignalOutputChanged();
            foreach (var subscriber in _outputSubscribers.Values)
            {
                subscriber.Writer.TryComplete();
            }

            _outputSubscribers.Clear();
        }

        private void FailPendingResizeLocked(string code, string message)
        {
            _resizeApplied?.TrySetException(new TerminalGatewayActionException(code, message));
            _resizeApplied = null;
        }

        private bool CanAcceptLateCloseAfterTimeout() => _failureCode is
            "terminal_close_timeout" or
            "terminal_open_timeout" or
            "terminal_reconnect_timeout" or
            "terminal_policy_lease_expired";
    }

    private enum TerminalLifecycle { Requested, Dispatching, Opening, Opened, Suspended, Closing, Closed, Failed }
    private enum TerminalOpenTransition { Rejected, AlreadyOpened, Opened }
    private enum TerminalCloseTransition { Rejected, AlreadyClosed, Closed }
    private enum TerminalFailTransition { Rejected, AlreadyFailed, Failed }
}

public sealed class GatewayTerminalOutputSubscription(ChannelReader<ReadOnlyMemory<byte>> reader, Action? dispose = null) : IDisposable
{
    private Action? _dispose = dispose;
    public ChannelReader<ReadOnlyMemory<byte>> Reader { get; } = reader;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

public sealed class AgentTerminalGatewayRegistration(
    ChannelReader<GatewayTerminalFrame> reader,
    Action dispose,
    Action<GatewayTerminalFrame>? markWritten = null,
    Func<bool>? isCurrent = null,
    CancellationToken completionToken = default,
    Guid registrationId = default,
    Func<bool>? tryActivate = null) : IDisposable
{
    private int _disposed;
    public ChannelReader<GatewayTerminalFrame> Reader { get; } = reader;
    public CancellationToken CompletionToken { get; } = completionToken;
    public Guid RegistrationId { get; } = registrationId;
    public bool IsCurrent => isCurrent?.Invoke() ?? true;
    public bool TryActivate() => Volatile.Read(ref _disposed) == 0 && (tryActivate?.Invoke() ?? IsCurrent);
    public void MarkWritten(GatewayTerminalFrame frame) => markWritten?.Invoke(frame);
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose(); }
}

internal sealed class AgentTerminalTransport(ClientKey client, Guid connectionId, ulong epoch, IReadOnlyList<string> availableShells, bool supportsIdempotentClose, DateTimeOffset registeredAtUtc)
{
    private const int OutboundCapacity = 64;
    private const int PendingRejectionLimit = 64;
    private const string RejectionReason = "terminal_session_rejected";
    // Preserve a deterministic control-plane reserve so a noisy terminal
    // cannot prevent Start, Close, or Resize lifecycle frames from reaching
    // the sole gRPC writer.
    private const int InputQueueLimit = 48;
    private long _sequence;
    private int _completed;
    private int _pendingInput;
    // Every producer, including writer-progress rejection retries, must
    // allocate and enqueue outbound frames under one gate. The client rejects
    // a later sequence that arrives before an earlier one, so Interlocked
    // allocation alone is insufficient when a retry races browser control.
    private readonly object _writeSync = new();
    private readonly object _rejectionSync = new();
    private readonly CancellationTokenSource _completion = new();
    private readonly ConcurrentDictionary<ulong, Action> _writtenCallbacks = new();
    private readonly ConcurrentDictionary<ulong, byte> _queuedInputSequences = new();
    private readonly ConcurrentDictionary<ulong, TerminalRejectionKey> _queuedRejectionSequences = new();
    private readonly Queue<TerminalRejectionKey> _pendingRejections = [];
    private readonly HashSet<TerminalRejectionKey> _rejectionKeys = [];
    private TaskCompletionSource<bool>? _rejectionProgress;
    private readonly Channel<GatewayTerminalFrame> _outbound = Channel.CreateBounded<GatewayTerminalFrame>(new BoundedChannelOptions(OutboundCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    public bool IsAdmitted { get; set; }
    public Guid RegistrationId { get; } = Guid.NewGuid();
    public ClientKey Client { get; } = client;
    public Guid ConnectionId { get; } = connectionId;
    public ulong ConnectionEpoch { get; } = epoch;
    public IReadOnlyList<string> AvailableShells { get; } = availableShells;
    public bool SupportsIdempotentClose { get; } = supportsIdempotentClose;
    public DateTimeOffset RegisteredAtUtc { get; } = registeredAtUtc;
    public ChannelReader<GatewayTerminalFrame> Reader => _outbound.Reader;
    public CancellationToken CompletionToken => _completion.Token;
    public bool Matches(Guid connectionId, ulong connectionEpoch) => ConnectionId == connectionId && ConnectionEpoch == connectionEpoch;
    public void Complete()
    {
        lock (_rejectionSync)
        {
            lock (_writeSync)
            {
                // Completion and writer-progress rejection flushes share the
                // same rejection → outbound lock order. Set the terminal bit
                // only after both gates are held so a flush is wholly before
                // completion or sees Closed; it cannot enqueue onto a
                // transport that has begun removal.
                if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
                {
                    return;
                }

                _pendingRejections.Clear();
                _rejectionKeys.Clear();
                _queuedRejectionSequences.Clear();
                PulseRejectionProgressLocked();
                _outbound.Writer.TryComplete();
            }
        }
        _completion.Cancel();
    }

    public void MarkWritten(GatewayTerminalFrame frame)
    {
        try
        {
            if (_writtenCallbacks.TryRemove(frame.Sequence, out var callback))
            {
                callback();
            }
        }
        finally
        {
            if (_queuedInputSequences.TryRemove(frame.Sequence, out _))
            {
                Interlocked.Decrement(ref _pendingInput);
            }

            ReleaseWrittenRejectionAndFlush(frame.Sequence);
        }
    }

    public TerminalTransportEnqueueResult TryStart(string id, ulong generation, string shellType, string? workingDirectory, int columns, int rows, Action written) =>
        TryWriteWithRejectionPriority(new GatewayTerminalFrame { Start = new TerminalSessionStart { SessionId = id, Generation = generation, ShellType = shellType, WorkingDirectory = workingDirectory ?? string.Empty, Columns = (uint)columns, Rows = (uint)rows } }, written);
    public TerminalTransportEnqueueResult TryInput(string id, ulong generation, ulong sequence, ReadOnlyMemory<byte> content) =>
        TryWriteWithRejectionPriority(new GatewayTerminalFrame { Input = new TerminalInput { SessionId = id, Generation = generation, SessionSequence = sequence, Content = ByteString.CopyFrom(content.Span) } });
    public TerminalTransportEnqueueResult TryResize(string id, ulong generation, ulong sequence, int columns, int rows) =>
        TryWriteWithRejectionPriority(new GatewayTerminalFrame { Resize = new TerminalResize { SessionId = id, Generation = generation, SessionSequence = sequence, Columns = (uint)columns, Rows = (uint)rows } });
    public TerminalTransportEnqueueResult TryClose(string id, ulong generation, string reason) =>
        TryWriteWithRejectionPriority(new GatewayTerminalFrame { Close = new TerminalSessionClose { SessionId = id, Generation = generation, Reason = reason[..Math.Min(128, reason.Length)] } });

    /// <summary>
    /// Reject an untracked PTY without allowing a full control reserve to make
    /// it orphaned. A deduplicated, bounded retry queue is drained only when
    /// the sole writer confirms progress, so it cannot grow with repeated
    /// reannouncements or spin while the peer is stalled.
    /// </summary>
    public TerminalRejectionEnqueueAttempt TryReject(string id, ulong generation)
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            return new(TerminalTransportEnqueueResult.Closed);
        }

        var key = new TerminalRejectionKey(id, generation);
        lock (_rejectionSync)
        {
            if (Volatile.Read(ref _completed) != 0)
            {
                return new(TerminalTransportEnqueueResult.Closed);
            }

            if (!_rejectionKeys.Add(key))
            {
                return new(TerminalTransportEnqueueResult.Accepted);
            }

            // Preserve FIFO cleanup: a freshly-arrived stale PTY must not
            // take a newly freed channel slot ahead of an older pending
            // rejection that has not yet been observed by the sole writer.
            if (_pendingRejections.Count > 0)
            {
                if (_pendingRejections.Count < PendingRejectionLimit)
                {
                    _pendingRejections.Enqueue(key);
                    return new(TerminalTransportEnqueueResult.Accepted);
                }

                _rejectionKeys.Remove(key);
                return new(TerminalTransportEnqueueResult.Backpressured, GetRejectionProgressLocked());
            }

            var enqueue = TryWriteRejection(key);
            if (enqueue == TerminalTransportEnqueueResult.Accepted)
            {
                return new(enqueue);
            }

            if (enqueue == TerminalTransportEnqueueResult.Backpressured && _pendingRejections.Count < PendingRejectionLimit)
            {
                _pendingRejections.Enqueue(key);
                return new(TerminalTransportEnqueueResult.Accepted);
            }

            _rejectionKeys.Remove(key);
            return new(
                enqueue,
                enqueue == TerminalTransportEnqueueResult.Backpressured ? GetRejectionProgressLocked() : null);
        }
    }

    private TerminalTransportEnqueueResult TryWriteRejection(TerminalRejectionKey key) =>
        TryWrite(
            new GatewayTerminalFrame
            {
                Close = new TerminalSessionClose
                {
                    SessionId = key.SessionId,
                    Generation = key.Generation,
                    Reason = RejectionReason
                }
            },
            beforeEnqueue: frame =>
            {
                if (!_queuedRejectionSequences.TryAdd(frame.Sequence, key))
                {
                    throw new InvalidOperationException("The terminal transport generated a duplicate rejection frame sequence.");
                }
            },
            enqueueFailed: frame => _queuedRejectionSequences.TryRemove(frame.Sequence, out _));

    private TerminalTransportEnqueueResult TryWriteWithRejectionPriority(
        GatewayTerminalFrame frame,
        Action? written = null)
    {
        lock (_rejectionSync)
        {
            // A reader has already freed a bounded channel slot before it
            // calls MarkWritten. Give older stale-PTY cleanup work first use
            // of that slot so steady normal control traffic cannot starve a
            // queued rejection indefinitely.
            if (FlushPendingRejectionsLocked())
            {
                PulseRejectionProgressLocked();
            }
            return TryWrite(frame, written);
        }
    }

    private void ReleaseWrittenRejectionAndFlush(ulong sequence)
    {
        lock (_rejectionSync)
        {
            if (_queuedRejectionSequences.TryRemove(sequence, out var key))
            {
                _rejectionKeys.Remove(key);
            }

            FlushPendingRejectionsLocked();
            PulseRejectionProgressLocked();
        }
    }

    private Task GetRejectionProgressLocked() =>
        (_rejectionProgress ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private void PulseRejectionProgressLocked()
    {
        var progress = _rejectionProgress;
        _rejectionProgress = null;
        progress?.TrySetResult(true);
    }

    private bool FlushPendingRejectionsLocked()
    {
        var progressed = false;
        while (_pendingRejections.TryPeek(out var key))
        {
            var enqueue = TryWriteRejection(key);
            if (enqueue == TerminalTransportEnqueueResult.Accepted)
            {
                _pendingRejections.Dequeue();
                progressed = true;
                continue;
            }

            if (enqueue == TerminalTransportEnqueueResult.Closed)
            {
                _pendingRejections.Clear();
                _rejectionKeys.Clear();
                _queuedRejectionSequences.Clear();
            }

            return progressed;
        }

        return progressed;
    }

    private TerminalTransportEnqueueResult TryWrite(
        GatewayTerminalFrame frame,
        Action? written = null,
        Action<GatewayTerminalFrame>? beforeEnqueue = null,
        Action<GatewayTerminalFrame>? enqueueFailed = null)
    {
        lock (_writeSync)
        {
            if (Volatile.Read(ref _completed) != 0) return TerminalTransportEnqueueResult.Closed;
            var isInput = frame.PayloadCase == GatewayTerminalFrame.PayloadOneofCase.Input;
            if (isInput && !TryReserveInput())
            {
                return TerminalTransportEnqueueResult.Backpressured;
            }

            frame.ProtocolVersion = "1.0";
            frame.TenantId = Client.TenantId;
            frame.ClientId = Client.AgentId.ToString("D");
            frame.ConnectionEpoch = ConnectionEpoch;
            frame.ConnectionId = ConnectionId.ToString("D");
            frame.Sequence = checked((ulong)Interlocked.Increment(ref _sequence));
            if (written is not null && !_writtenCallbacks.TryAdd(frame.Sequence, written))
            {
                if (isInput)
                {
                    Interlocked.Decrement(ref _pendingInput);
                }

                throw new InvalidOperationException("The terminal transport generated a duplicate frame sequence.");
            }

            if (isInput && !_queuedInputSequences.TryAdd(frame.Sequence, 0))
            {
                if (written is not null)
                {
                    _writtenCallbacks.TryRemove(frame.Sequence, out _);
                }

                Interlocked.Decrement(ref _pendingInput);
                throw new InvalidOperationException("The terminal transport generated a duplicate input frame sequence.");
            }

            try
            {
                beforeEnqueue?.Invoke(frame);
            }
            catch
            {
                if (written is not null)
                {
                    _writtenCallbacks.TryRemove(frame.Sequence, out _);
                }

                if (isInput && _queuedInputSequences.TryRemove(frame.Sequence, out _))
                {
                    Interlocked.Decrement(ref _pendingInput);
                }

                throw;
            }

            if (_outbound.Writer.TryWrite(frame))
            {
                return TerminalTransportEnqueueResult.Accepted;
            }

            if (written is not null)
            {
                _writtenCallbacks.TryRemove(frame.Sequence, out _);
            }

            if (isInput && _queuedInputSequences.TryRemove(frame.Sequence, out _))
            {
                Interlocked.Decrement(ref _pendingInput);
            }

            enqueueFailed?.Invoke(frame);

            return Volatile.Read(ref _completed) != 0
                ? TerminalTransportEnqueueResult.Closed
                : TerminalTransportEnqueueResult.Backpressured;
        }
    }

    private bool TryReserveInput()
    {
        while (true)
        {
            var current = Volatile.Read(ref _pendingInput);
            if (current >= InputQueueLimit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _pendingInput, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private readonly record struct TerminalRejectionKey(string SessionId, ulong Generation);
}

internal enum TerminalTransportEnqueueResult
{
    Accepted,
    Backpressured,
    Closed
}

internal readonly record struct TerminalRejectionEnqueueAttempt(
    TerminalTransportEnqueueResult Outcome,
    Task? Progress = null);

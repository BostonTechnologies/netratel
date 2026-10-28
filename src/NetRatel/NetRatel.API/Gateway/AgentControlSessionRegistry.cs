using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.ComputerUse;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace NetRatel.API.Gateway;

public sealed record AgentControlPingResult(
    ClientKey Client,
    Guid RequestId,
    DateTimeOffset SentAtUtc,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset AgentRespondedAtUtc)
{
    public TimeSpan RoundTripTime => ReceivedAtUtc - SentAtUtc;
}

public sealed class AgentControlSessionUnavailableException(ClientKey client)
    : InvalidOperationException($"No active control session exists for tenant {client.TenantId}, agent {client.AgentId:D}.");

public sealed record AgentControlAvailability(Guid ConnectionId, ulong ConnectionEpoch, IReadOnlyList<string> Capabilities);

public interface IAgentControlSessionRegistry
{
    AgentControlSessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool provisional = false);

    Task<AgentControlPingResult> RequestPingAsync(
        ClientKey client,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    bool IsAvailable(ClientKey client);

    bool TryGetLatestPing(ClientKey client, out AgentControlPingResult result);

    AgentControlAvailability? GetAvailability(ClientKey client) => null;

    Task<UninstallAcknowledgement> RequestUninstallAsync(ClientKey client, Guid connectionId, ulong connectionEpoch,
        UninstallRequest request, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromException<UninstallAcknowledgement>(new AgentControlSessionUnavailableException(client));

    Task<ComputerUseResult> RequestComputerUseAsync(ClientKey client, Guid connectionId, ulong connectionEpoch,
        ComputerUseCommand command, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromException<ComputerUseResult>(new AgentControlSessionUnavailableException(client));
}

public sealed partial class AgentControlSessionRegistry(TimeProvider timeProvider) : IAgentControlSessionRegistry
{
    private readonly ConcurrentDictionary<ClientKey, AgentControlSession> _sessions = new();
    private readonly ConcurrentDictionary<ClientKey, AgentControlPingResult> _latestPings = new();

    public AgentControlSessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool provisional = false)
    {
        var session = new AgentControlSession(client, connectionId, connectionEpoch, timeProvider);
        while (true)
        {
            if (!_sessions.TryGetValue(client, out var previous))
            {
                if (_sessions.TryAdd(client, session))
                {
                    break;
                }

                continue;
            }

            if (!GatewaySessionRegistrationFence.CanReplace(
                    connectionId,
                    connectionEpoch,
                    previous.ConnectionId,
                    previous.ConnectionEpoch))
            {
                session.Complete();
                throw new AgentGatewayRegistrationFencedException();
            }

            if (_sessions.TryUpdate(client, session, previous))
            {
                previous.Complete();
                break;
            }
        }

        bool IsCurrent() => _sessions.TryGetValue(client, out var current) && ReferenceEquals(current, session);
        if (!provisional)
        {
            session.Activate();
        }

        return new AgentControlSessionRegistration(session.RegistrationId, session.Reader, session.TryCompletePing, session.CompletionToken,
            IsCurrent, () =>
            {
                if (!IsCurrent())
                    return false;
                session.Activate();
                return IsCurrent() && !session.CompletionToken.IsCancellationRequested;
            }, () =>
            {
                ((ICollection<KeyValuePair<ClientKey, AgentControlSession>>)_sessions)
                    .Remove(new KeyValuePair<ClientKey, AgentControlSession>(client, session));
                session.Complete();
            }, session.SetCapabilities, session.TryCompleteUninstall, session.TryCompleteComputerUse);
    }

    public async Task<AgentControlPingResult> RequestPingAsync(
        ClientKey client,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(client, out var session) || !session.IsActive)
        {
            throw new AgentControlSessionUnavailableException(client);
        }

        var result = await session.RequestPingAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (!_sessions.TryGetValue(client, out var current) || !ReferenceEquals(current, session) || !session.IsActive)
            throw new OperationCanceledException("The agent control session was replaced.");
        _latestPings[client] = result;
        return result;
    }

    public bool TryGetLatestPing(ClientKey client, out AgentControlPingResult result) =>
        _latestPings.TryGetValue(client, out result!);

    public bool IsAvailable(ClientKey client) =>
        _sessions.TryGetValue(client, out var session) && session.IsActive;

    public AgentControlAvailability? GetAvailability(ClientKey client) =>
        _sessions.TryGetValue(client, out var session) && session.IsActive
            ? new(session.ConnectionId, session.ConnectionEpoch, session.Capabilities) : null;

    public async Task<UninstallAcknowledgement> RequestUninstallAsync(ClientKey client, Guid connectionId, ulong connectionEpoch,
        UninstallRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(client, out var session) || !session.IsActive || session.ConnectionId != connectionId ||
            session.ConnectionEpoch != connectionEpoch || !session.Capabilities.Contains("uninstall-v1", StringComparer.Ordinal))
            throw new AgentControlSessionUnavailableException(client);
        var result = await session.RequestUninstallAsync(request, timeout, cancellationToken).ConfigureAwait(false);
        if (!_sessions.TryGetValue(client, out var current) || !ReferenceEquals(current, session) || !session.IsActive)
            throw new OperationCanceledException("The agent control session was replaced.");
        return result;
    }
}

public sealed class AgentControlSessionRegistration(
    Guid registrationId,
    ChannelReader<GatewayControlFrame> reader,
    Func<Guid, DateTimeOffset, bool> completePing,
    CancellationToken completionToken,
    Func<bool> isCurrent,
    Func<bool> activate,
    Action unregister,
    Action<IReadOnlyList<string>>? setCapabilities = null,
    Func<UninstallAcknowledgement, bool>? completeUninstall = null,
    Func<ComputerUseCompletion, bool>? completeComputerUse = null) : IDisposable
{
    private int _disposed;

    public Guid RegistrationId { get; } = registrationId;
    public ChannelReader<GatewayControlFrame> Reader { get; } = reader;
    public CancellationToken CompletionToken { get; } = completionToken;
    public bool IsCurrent => Volatile.Read(ref _disposed) == 0 && !CompletionToken.IsCancellationRequested && isCurrent();
    public bool Activate() => IsCurrent && activate();

    public bool TryCompletePing(Guid requestId, DateTimeOffset agentRespondedAtUtc) =>
        IsCurrent && completePing(requestId, agentRespondedAtUtc);

    public void SetCapabilities(IReadOnlyList<string> capabilities) => setCapabilities?.Invoke(capabilities);
    public bool TryCompleteUninstall(UninstallAcknowledgement acknowledgement) => IsCurrent && completeUninstall?.Invoke(acknowledgement) == true;
    public bool TryCompleteComputerUse(ComputerUseCompletion completion) => IsCurrent && completeComputerUse?.Invoke(completion) == true;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            unregister();
        }
    }
}

internal sealed partial class AgentControlSession(
    ClientKey client,
    Guid connectionId,
    ulong connectionEpoch,
    TimeProvider timeProvider)
{
    private const int OutboundCapacity = 64;
    private readonly Channel<GatewayControlFrame> _outbound = Channel.CreateBounded<GatewayControlFrame>(
        new BoundedChannelOptions(OutboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly ConcurrentDictionary<Guid, PendingPing> _pendingPings = new();
    private readonly ConcurrentDictionary<Guid, PendingUninstall> _pendingUninstalls = new();
    private readonly SemaphoreSlim _outboundGate = new(1, 1);
    private readonly CancellationTokenSource _completion = new();
    private int _active;
    private int _completed;
    private long _outboundSequence;

    public ChannelReader<GatewayControlFrame> Reader => _outbound.Reader;
    public Guid RegistrationId { get; } = Guid.NewGuid();
    public CancellationToken CompletionToken => _completion.Token;
    public bool IsActive => Volatile.Read(ref _active) != 0 && Volatile.Read(ref _completed) == 0;
    public void Activate() => Volatile.Write(ref _active, 1);
    public Guid ConnectionId => connectionId;
    public ulong ConnectionEpoch => connectionEpoch;
    public IReadOnlyList<string> Capabilities { get; private set; } = [];
    public void SetCapabilities(IReadOnlyList<string> capabilities)
    {
        if (IsActive) throw new InvalidOperationException("Control capabilities are fixed at admission.");
        Capabilities = capabilities.Distinct(StringComparer.Ordinal).ToArray();
    }

    public async Task<UninstallAcknowledgement> RequestUninstallAsync(UninstallRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.OperationId, out var operationId) || operationId == Guid.Empty)
            throw new ArgumentException("An uninstall operation ID is required.", nameof(request));
        var pending = new PendingUninstall(request.Clone(), new(TaskCreationOptions.RunContinuationsAsynchronously));
        while (!_pendingUninstalls.TryAdd(operationId, pending))
        {
            if (!_pendingUninstalls.TryGetValue(operationId, out var existing)) continue;
            if (!existing.Request.Equals(request))
                throw new InvalidOperationException("The uninstall operation is already being dispatched.");
            return await existing.Completion.Task.WaitAsync(timeout, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, CompletionToken);
        try
        {
            await WriteFrameAsync(new GatewayControlFrame
            {
                ProtocolVersion = "1.0", TenantId = client.TenantId, ClientId = client.AgentId.ToString("D"),
                ConnectionEpoch = connectionEpoch, ConnectionId = connectionId.ToString("D"),
                UninstallRequest = request.Clone()
            }, linked.Token).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally { _pendingUninstalls.TryRemove(operationId, out _); }
    }

    public bool TryCompleteUninstall(UninstallAcknowledgement acknowledgement) =>
        IsActive && Guid.TryParse(acknowledgement.OperationId, out var operationId) &&
        _pendingUninstalls.TryGetValue(operationId, out var pending) && pending.Completion.TrySetResult(acknowledgement.Clone());

    public async Task<AgentControlPingResult> RequestPingAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var sentAtUtc = timeProvider.GetUtcNow();
        var deadlineUtc = sentAtUtc.Add(timeout);
        var completion = new TaskCompletionSource<AgentControlPingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingPing(sentAtUtc, completion);
        if (!_pendingPings.TryAdd(requestId, pending))
        {
            throw new InvalidOperationException("The generated ping request ID was already in use.");
        }

        try
        {
            await WriteFrameAsync(new GatewayControlFrame
            {
                ProtocolVersion = "1.0",
                TenantId = client.TenantId,
                ClientId = client.AgentId.ToString("D"),
                ConnectionEpoch = connectionEpoch,
                ConnectionId = connectionId.ToString("D"),
                PingRequest = new PingRequest
                {
                    RequestId = requestId.ToString("D"),
                    SentAtUtc = Timestamp.FromDateTimeOffset(sentAtUtc),
                    DeadlineUtc = Timestamp.FromDateTimeOffset(deadlineUtc)
                }
            }, cancellationToken).ConfigureAwait(false);

            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingPings.TryRemove(requestId, out _);
        }
    }

    public bool TryCompletePing(Guid requestId, DateTimeOffset agentRespondedAtUtc)
    {
        if (!IsActive || !_pendingPings.TryGetValue(requestId, out var pending))
        {
            return false;
        }

        var receivedAtUtc = timeProvider.GetUtcNow();
        return pending.Completion.TrySetResult(new AgentControlPingResult(
            client,
            requestId,
            pending.SentAtUtc,
            receivedAtUtc,
            agentRespondedAtUtc));
    }

    private async ValueTask WriteFrameAsync(GatewayControlFrame frame, CancellationToken cancellationToken)
    {
        // Pings and lifecycle commands share one ordered stream. Assign sequence numbers in write order.
        await _outboundGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            frame.Sequence = checked((ulong)++_outboundSequence);
            await _outbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally { _outboundGate.Release(); }
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        _outbound.Writer.TryComplete();
        _completion.Cancel();
        foreach (var pending in _pendingPings.Values)
        {
            pending.Completion.TrySetException(new OperationCanceledException("The agent control session ended."));
        }
        foreach (var pending in _pendingUninstalls.Values)
            pending.Completion.TrySetException(new OperationCanceledException("The agent control session ended."));
        foreach (var pending in _pendingComputerUse.Values)
            pending.Completion.TrySetException(new OperationCanceledException("The agent control session ended."));
    }

    private sealed record PendingPing(
        DateTimeOffset SentAtUtc,
        TaskCompletionSource<AgentControlPingResult> Completion);

    private sealed record PendingUninstall(UninstallRequest Request, TaskCompletionSource<UninstallAcknowledgement> Completion);
}

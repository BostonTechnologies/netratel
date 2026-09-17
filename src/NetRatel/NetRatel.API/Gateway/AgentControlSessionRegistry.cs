using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
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

public interface IAgentControlSessionRegistry
{
    AgentControlSessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool provisional = false);

    Task<AgentControlPingResult> RequestPingAsync(
        ClientKey client,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    bool TryGetLatestPing(ClientKey client, out AgentControlPingResult result);
}

public sealed class AgentControlSessionRegistry(TimeProvider timeProvider) : IAgentControlSessionRegistry
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
            });
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
}

public sealed class AgentControlSessionRegistration(
    Guid registrationId,
    ChannelReader<GatewayControlFrame> reader,
    Func<Guid, DateTimeOffset, bool> completePing,
    CancellationToken completionToken,
    Func<bool> isCurrent,
    Func<bool> activate,
    Action unregister) : IDisposable
{
    private int _disposed;

    public Guid RegistrationId { get; } = registrationId;
    public ChannelReader<GatewayControlFrame> Reader { get; } = reader;
    public CancellationToken CompletionToken { get; } = completionToken;
    public bool IsCurrent => Volatile.Read(ref _disposed) == 0 && !CompletionToken.IsCancellationRequested && isCurrent();
    public bool Activate() => IsCurrent && activate();

    public bool TryCompletePing(Guid requestId, DateTimeOffset agentRespondedAtUtc) =>
        IsCurrent && completePing(requestId, agentRespondedAtUtc);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            unregister();
        }
    }
}

internal sealed class AgentControlSession(
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
            var sequence = checked((ulong)Interlocked.Increment(ref _outboundSequence));
            await _outbound.Writer.WriteAsync(new GatewayControlFrame
            {
                ProtocolVersion = "1.0",
                TenantId = client.TenantId,
                ClientId = client.AgentId.ToString("D"),
                ConnectionEpoch = connectionEpoch,
                ConnectionId = connectionId.ToString("D"),
                Sequence = sequence,
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
    }

    private sealed record PendingPing(
        DateTimeOffset SentAtUtc,
        TaskCompletionSource<AgentControlPingResult> Completion);
}

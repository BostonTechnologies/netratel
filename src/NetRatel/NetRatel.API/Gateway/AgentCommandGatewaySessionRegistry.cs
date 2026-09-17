using System.Collections.Concurrent;
using System.Threading.Channels;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

/// <summary>
/// Holds only transient, fenced command transport sessions. Command lifecycle
/// state remains in the Akka command region; this registry never persists a
/// command and never falls back to SpacetimeDB.
/// </summary>
public interface IAgentCommandGatewaySessionRegistry
{
    AgentCommandGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool provisional = false);

    bool IsAvailable(ClientKey client);

    Task DispatchAsync(ClientKey client, CommandGatewayDispatch dispatch, CancellationToken cancellationToken);

    Task CancelAsync(ClientKey client, string commandId, string reason, CancellationToken cancellationToken);
}

public sealed record CommandGatewayDispatch(
    string CommandId,
    string CorrelationId,
    DateTimeOffset RequestTimestamp,
    ulong NextVersion,
    ulong NextSequence,
    string TaskType,
    string PayloadJson,
    int Environment,
    int TenantId);

public sealed class AgentCommandGatewaySessionUnavailableException(ClientKey client)
    : InvalidOperationException($"No active command gateway session exists for tenant {client.TenantId}, agent {client.AgentId:D}.");

public sealed class AgentCommandGatewaySessionRegistry : IAgentCommandGatewaySessionRegistry
{
    private readonly ConcurrentDictionary<ClientKey, AgentCommandGatewaySession> _sessions = new();

    public AgentCommandGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool provisional = false)
    {
        var session = new AgentCommandGatewaySession(client, connectionId, connectionEpoch);
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

        return new AgentCommandGatewayRegistration(session.RegistrationId, session.Reader, session.CompletionToken,
            IsCurrent, () =>
            {
                if (!IsCurrent())
                    return false;
                session.Activate();
                return IsCurrent() && !session.CompletionToken.IsCancellationRequested;
            }, () =>
            {
                ((ICollection<KeyValuePair<ClientKey, AgentCommandGatewaySession>>)_sessions)
                    .Remove(new KeyValuePair<ClientKey, AgentCommandGatewaySession>(client, session));
                session.Complete();
            });
    }

    public bool IsAvailable(ClientKey client) => _sessions.TryGetValue(client, out var session) && session.IsActive;

    public Task DispatchAsync(ClientKey client, CommandGatewayDispatch dispatch, CancellationToken cancellationToken) =>
        GetSession(client).DispatchAsync(dispatch, cancellationToken);

    public Task CancelAsync(ClientKey client, string commandId, string reason, CancellationToken cancellationToken) =>
        GetSession(client).CancelAsync(commandId, reason, cancellationToken);

    private AgentCommandGatewaySession GetSession(ClientKey client) =>
        _sessions.TryGetValue(client, out var session) && session.IsActive
            ? session
            : throw new AgentCommandGatewaySessionUnavailableException(client);
}

public sealed class AgentCommandGatewayRegistration(
    Guid registrationId,
    ChannelReader<GatewayCommandFrame> reader,
    CancellationToken completionToken,
    Func<bool> isCurrent,
    Func<bool> activate,
    Action unregister) : IDisposable
{
    private int _disposed;

    public Guid RegistrationId { get; } = registrationId;
    public ChannelReader<GatewayCommandFrame> Reader { get; } = reader;
    public CancellationToken CompletionToken { get; } = completionToken;
    public bool IsCurrent => Volatile.Read(ref _disposed) == 0 && !CompletionToken.IsCancellationRequested && isCurrent();
    public bool Activate() => IsCurrent && activate();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            unregister();
        }
    }
}

internal sealed class AgentCommandGatewaySession(
    ClientKey client, Guid connectionId, ulong connectionEpoch, Channel<GatewayCommandFrame>? outbound = null)
{
    private readonly Channel<GatewayCommandFrame> _outbound = outbound ?? Channel.CreateBounded<GatewayCommandFrame>(
        new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource _completion = new();
    private int _active;
    private int _completed;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ulong _outboundSequence;

    public ChannelReader<GatewayCommandFrame> Reader => _outbound.Reader;
    public Guid RegistrationId { get; } = Guid.NewGuid();
    public CancellationToken CompletionToken => _completion.Token;
    public bool IsActive => Volatile.Read(ref _active) != 0 && Volatile.Read(ref _completed) == 0;
    public void Activate() => Volatile.Write(ref _active, 1);
    public Guid ConnectionId => connectionId;
    public ulong ConnectionEpoch => connectionEpoch;

    public Task DispatchAsync(CommandGatewayDispatch dispatch, CancellationToken cancellationToken) =>
        WriteAsync(new GatewayCommandFrame
        {
            ProtocolVersion = "1.0",
            TenantId = client.TenantId,
            ClientId = client.AgentId.ToString("D"),
            ConnectionEpoch = connectionEpoch,
            ConnectionId = connectionId.ToString("D"),
            Dispatch = new CommandDispatch
            {
                CommandId = dispatch.CommandId,
                CorrelationId = dispatch.CorrelationId,
                RequestTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(dispatch.RequestTimestamp),
                NextVersion = dispatch.NextVersion,
                NextSequence = dispatch.NextSequence,
                TaskType = dispatch.TaskType,
                PayloadJson = dispatch.PayloadJson,
                Environment = dispatch.Environment,
                TenantId = dispatch.TenantId
            }
        }, cancellationToken);

    public Task CancelAsync(string commandId, string reason, CancellationToken cancellationToken) =>
        WriteAsync(new GatewayCommandFrame
        {
            ProtocolVersion = "1.0",
            TenantId = client.TenantId,
            ClientId = client.AgentId.ToString("D"),
            ConnectionEpoch = connectionEpoch,
            ConnectionId = connectionId.ToString("D"),
            Cancel = new CommandCancel { CommandId = commandId, Reason = reason }
        }, cancellationToken);

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        _outbound.Writer.TryComplete();
        _completion.Cancel();
    }

    private async Task WriteAsync(GatewayCommandFrame frame, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Sequence allocation and admission share one order, including when
            // the bounded channel makes a producer wait for space.
            frame.Sequence = checked(++_outboundSequence);
            await _outbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

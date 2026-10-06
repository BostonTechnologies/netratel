using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

/// <summary>
/// Transient, fenced transport registry for authoritative job-step dispatch.
/// Durable job state belongs to the Akka job region and PostgreSQL read model,
/// never to this connection registry.
/// </summary>
public interface IAgentJobGatewaySessionRegistry
{
    AgentJobGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool provisional = false);
    bool IsAvailable(ClientKey client);
    // A physical registration is routing information, never durable authority.
    // Existing nonpersisting fixture adapters return no production fence.
    OwnerKey? GetRegisteredOwner(ClientKey client) => null;
    Task DispatchAsync(ClientKey client, JobGatewayStepDispatch dispatch, CancellationToken cancellationToken);
    Task CancelAsync(ClientKey client, ulong jobRunId, string reason, CancellationToken cancellationToken);
    Task CancelAsync(OwnerKey expectedOwner, ulong jobRunId, string reason, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The transport adapter cannot target an exact dispatch owner.");
}

public sealed record JobGatewayStepDispatch(
    ulong JobRunId,
    ulong JobStepId,
    ulong JobStepRunId,
    int Ordinal,
    string RequestId,
    string CorrelationId,
    string TaskType,
    string PayloadJson,
    int Environment,
    ulong NextVersion,
    ulong NextSequence,
    DateTimeOffset RequestedAtUtc,
    OwnerKey? ExpectedOwner = null);

public sealed class AgentJobGatewaySessionUnavailableException(ClientKey client)
    : InvalidOperationException($"No active job gateway session exists for tenant {client.TenantId}, agent {client.AgentId:D}.");

public sealed class AgentJobGatewaySessionRegistry : IAgentJobGatewaySessionRegistry
{
    private readonly ConcurrentDictionary<ClientKey, AgentJobGatewaySession> _sessions = new();

    public AgentJobGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool provisional = false)
    {
        var session = new AgentJobGatewaySession(client, connectionId, connectionEpoch);
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

        return new AgentJobGatewayRegistration(session.RegistrationId, session.Reader, session.CompletionToken,
            IsCurrent, () =>
            {
                if (!IsCurrent())
                    return false;
                session.Activate();
                return IsCurrent() && !session.CompletionToken.IsCancellationRequested;
            }, () =>
            {
                ((ICollection<KeyValuePair<ClientKey, AgentJobGatewaySession>>)_sessions)
                    .Remove(new KeyValuePair<ClientKey, AgentJobGatewaySession>(client, session));
                session.Complete();
            });
    }

    public bool IsAvailable(ClientKey client) => _sessions.TryGetValue(client, out var session) && session.IsActive;

    public OwnerKey? GetRegisteredOwner(ClientKey client) =>
        _sessions.TryGetValue(client, out var session) && session.IsActive
            ? new OwnerKey(client, session.ConnectionId, checked((long)session.ConnectionEpoch)) : null;

    public Task DispatchAsync(ClientKey client, JobGatewayStepDispatch dispatch, CancellationToken cancellationToken)
    {
        var session = GetSession(client);
        if (dispatch.ExpectedOwner is { } expected &&
            (expected.Client != client || expected.ConnectionId != session.ConnectionId ||
             expected.Epoch != checked((long)session.ConnectionEpoch)))
            throw new AgentJobGatewaySessionUnavailableException(client);
        // Keep this captured session; never look up a successor after the check.
        return session.DispatchAsync(dispatch, cancellationToken);
    }

    public Task CancelAsync(ClientKey client, ulong jobRunId, string reason, CancellationToken cancellationToken) =>
        GetSession(client).CancelAsync(jobRunId, reason, cancellationToken);

    public Task CancelAsync(OwnerKey expectedOwner, ulong jobRunId, string reason, CancellationToken cancellationToken)
    {
        var session = GetSession(expectedOwner.Client);
        if (expectedOwner.ConnectionId != session.ConnectionId ||
            expectedOwner.Epoch != checked((long)session.ConnectionEpoch))
            throw new AgentJobGatewaySessionUnavailableException(expectedOwner.Client);
        return session.CancelAsync(jobRunId, reason, cancellationToken);
    }

    private AgentJobGatewaySession GetSession(ClientKey client) =>
        _sessions.TryGetValue(client, out var session) && session.IsActive
            ? session
            : throw new AgentJobGatewaySessionUnavailableException(client);
}

public sealed class AgentJobGatewayRegistration(
    Guid registrationId,
    ChannelReader<GatewayJobFrame> reader,
    CancellationToken completionToken,
    Func<bool> isCurrent,
    Func<bool> activate,
    Action unregister) : IDisposable
{
    private int _disposed;

    public Guid RegistrationId { get; } = registrationId;
    public ChannelReader<GatewayJobFrame> Reader { get; } = reader;
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

internal sealed class AgentJobGatewaySession(ClientKey client, Guid connectionId, ulong connectionEpoch)
{
    private readonly Channel<GatewayJobFrame> _outbound = Channel.CreateBounded<GatewayJobFrame>(
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
    private long _outboundSequence;

    public ChannelReader<GatewayJobFrame> Reader => _outbound.Reader;
    public Guid RegistrationId { get; } = Guid.NewGuid();
    public CancellationToken CompletionToken => _completion.Token;
    public bool IsActive => Volatile.Read(ref _active) != 0 && Volatile.Read(ref _completed) == 0;
    public void Activate() => Volatile.Write(ref _active, 1);
    public Guid ConnectionId => connectionId;
    public ulong ConnectionEpoch => connectionEpoch;

    public Task DispatchAsync(JobGatewayStepDispatch dispatch, CancellationToken cancellationToken) =>
        WriteAsync(new GatewayJobFrame
        {
            ProtocolVersion = "1.0",
            TenantId = client.TenantId,
            ClientId = client.AgentId.ToString("D"),
            ConnectionEpoch = connectionEpoch,
            ConnectionId = connectionId.ToString("D"),
            Sequence = NextSequence(),
            Dispatch = new JobStepDispatch
            {
                JobRunId = dispatch.JobRunId,
                JobStepId = dispatch.JobStepId,
                JobStepRunId = dispatch.JobStepRunId,
                TenantId = client.TenantId,
                Ordinal = dispatch.Ordinal,
                RequestId = dispatch.RequestId,
                CorrelationId = dispatch.CorrelationId,
                TaskType = dispatch.TaskType,
                PayloadJson = dispatch.PayloadJson,
                Environment = dispatch.Environment,
                NextVersion = dispatch.NextVersion,
                NextSequence = dispatch.NextSequence,
                RequestedAtUtc = Timestamp.FromDateTimeOffset(dispatch.RequestedAtUtc)
            }
        }, cancellationToken);

    public Task CancelAsync(ulong jobRunId, string reason, CancellationToken cancellationToken) =>
        WriteAsync(new GatewayJobFrame
        {
            ProtocolVersion = "1.0",
            TenantId = client.TenantId,
            ClientId = client.AgentId.ToString("D"),
            ConnectionEpoch = connectionEpoch,
            ConnectionId = connectionId.ToString("D"),
            Sequence = NextSequence(),
            Cancel = new JobCancel { JobRunId = jobRunId, Reason = reason }
        }, cancellationToken);

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        _outbound.Writer.TryComplete();
        _completion.Cancel();
    }

    private Task WriteAsync(GatewayJobFrame frame, CancellationToken cancellationToken) =>
        _outbound.Writer.WriteAsync(frame, cancellationToken).AsTask();

    private ulong NextSequence() => checked((ulong)Interlocked.Increment(ref _outboundSequence));
}

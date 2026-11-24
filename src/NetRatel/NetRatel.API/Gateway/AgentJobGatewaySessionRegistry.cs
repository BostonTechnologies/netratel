using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Secrets;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.API.Gateway;

/// <summary>
/// Transient, fenced transport registry for authoritative job-step dispatch.
/// Durable job state belongs to the Akka job region and PostgreSQL read model,
/// never to this connection registry.
/// </summary>
public interface IAgentJobGatewaySessionRegistry
{
    AgentJobGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool provisional = false);
    AgentJobGatewayRegistration RegisterWithCapabilities(ClientKey client, Guid connectionId, ulong connectionEpoch,
        bool provisional, bool supportsProtectedParameters) => Register(client, connectionId, connectionEpoch, provisional);
    AgentJobGatewayRegistration RegisterWithCapabilities(ClientKey client, Guid connectionId, ulong connectionEpoch,
        bool provisional, bool supportsProtectedParameters, IReadOnlyCollection<string> capabilities) =>
        RegisterWithCapabilities(client, connectionId, connectionEpoch, provisional, supportsProtectedParameters);
    bool IsAvailable(ClientKey client);
    // A physical registration is routing information, never durable authority.
    // Existing nonpersisting fixture adapters return no production fence.
    OwnerKey? GetRegisteredOwner(ClientKey client) => null;
    Task DispatchAsync(ClientKey client, JobGatewayStepDispatch dispatch, CancellationToken cancellationToken);
    bool SupportsProtectedParameters(OwnerKey owner) => false;
    Task DispatchProtectedAsync(OwnerKey owner, JobGatewayStepDispatch dispatch, IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken) => throw new ProtectedJobDispatchUnavailableException();
    bool SupportsExecutionAccount(OwnerKey owner, ExecutionIdentityMode mode) => false;
    Task DispatchProtectedAsync(OwnerKey owner, JobGatewayStepDispatch dispatch, IReadOnlyDictionary<string, string> parameters,
        RunAsCredential credential, CancellationToken cancellationToken) => throw new ProtectedJobDispatchUnavailableException();
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

public sealed class ProtectedJobDispatchUnavailableException()
    : InvalidOperationException("The current agent connection does not support protected job parameters.");

public sealed class AgentJobGatewaySessionRegistry(TimeProvider? clock = null) : IAgentJobGatewaySessionRegistry
{
    private readonly ConcurrentDictionary<ClientKey, AgentJobGatewaySession> _sessions = new();

    public AgentJobGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool provisional = false) =>
        RegisterWithCapabilities(client, connectionId, connectionEpoch, provisional, supportsProtectedParameters: false);

    public AgentJobGatewayRegistration RegisterWithCapabilities(ClientKey client, Guid connectionId, ulong connectionEpoch,
        bool provisional, bool supportsProtectedParameters) =>
        RegisterWithCapabilities(client, connectionId, connectionEpoch, provisional, supportsProtectedParameters, []);

    public AgentJobGatewayRegistration RegisterWithCapabilities(ClientKey client, Guid connectionId, ulong connectionEpoch,
        bool provisional, bool supportsProtectedParameters, IReadOnlyCollection<string> capabilities)
    {
        var session = new AgentJobGatewaySession(client, connectionId, connectionEpoch, supportsProtectedParameters, capabilities);
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
            }, session.PublishReceiptAsync);
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

    public bool SupportsProtectedParameters(OwnerKey owner) => _sessions.TryGetValue(owner.Client, out var session) &&
        session.IsActive && session.SupportsProtectedParameters && session.Matches(owner);

    public bool SupportsExecutionAccount(OwnerKey owner, ExecutionIdentityMode mode) => _sessions.TryGetValue(owner.Client, out var session) &&
        session.IsActive && session.Matches(owner) && session.Supports(ExecutionAccountCapabilities.For(mode));

    public Task DispatchProtectedAsync(OwnerKey owner, JobGatewayStepDispatch dispatch, IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken) => DispatchProtectedCoreAsync(owner, dispatch, parameters, null, cancellationToken);

    public Task DispatchProtectedAsync(OwnerKey owner, JobGatewayStepDispatch dispatch, IReadOnlyDictionary<string, string> parameters,
        RunAsCredential credential, CancellationToken cancellationToken) => DispatchProtectedCoreAsync(owner, dispatch, parameters, credential, cancellationToken);

    private Task DispatchProtectedCoreAsync(OwnerKey owner, JobGatewayStepDispatch dispatch, IReadOnlyDictionary<string, string> parameters,
        RunAsCredential? credential, CancellationToken cancellationToken)
    {
        if (dispatch.ExpectedOwner != owner || dispatch.TaskType is not (TaskKinds.ExecShellCommand or TaskKinds.ExecLibraryScript) || parameters.Count != 0 &&
            (dispatch.TaskType != TaskKinds.ExecLibraryScript || !ProtectedJobParameterRules.AreValid(parameters)))
            throw new ArgumentException("Protected parameters require a bounded library step and its exact dispatch owner.");
        if (!_sessions.TryGetValue(owner.Client, out var session) || !session.IsActive || !session.Matches(owner) || !session.SupportsProtectedParameters)
            throw new ProtectedJobDispatchUnavailableException();
        if (credential is not null && !session.Supports(ExecutionAccountServiceCapability.For(credential.Mode)))
            throw new ProtectedJobDispatchUnavailableException();
        var expires = (clock ?? TimeProvider.System).GetUtcNow() + ProtectedJobParameterRules.DispatchLifetime;
        return session.DispatchAsync(dispatch, cancellationToken, parameters, expires, credential);
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
    Action unregister,
    Func<JobLifecycleReceipt, CancellationToken, Task>? publishReceipt = null) : IDisposable
{
    private int _disposed;

    public Guid RegistrationId { get; } = registrationId;
    public ChannelReader<GatewayJobFrame> Reader { get; } = reader;
    public CancellationToken CompletionToken { get; } = completionToken;
    public bool IsCurrent => Volatile.Read(ref _disposed) == 0 && !CompletionToken.IsCancellationRequested && isCurrent();
    public bool Activate() => IsCurrent && activate();

    public Task PublishReceiptAsync(JobLifecycleReceipt receipt, CancellationToken cancellationToken) =>
        IsCurrent && publishReceipt is not null
            ? publishReceipt(receipt, cancellationToken)
            : throw new InvalidOperationException("The gateway receipt registration is no longer current.");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            unregister();
        }
    }
}

internal sealed class AgentJobGatewaySession(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsProtectedParameters,
    IReadOnlyCollection<string>? capabilities = null)
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
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ChannelReader<GatewayJobFrame> Reader => _outbound.Reader;
    public Guid RegistrationId { get; } = Guid.NewGuid();
    public CancellationToken CompletionToken => _completion.Token;
    public bool IsActive => Volatile.Read(ref _active) != 0 && Volatile.Read(ref _completed) == 0;
    public void Activate() => Volatile.Write(ref _active, 1);
    public Guid ConnectionId => connectionId;
    public ulong ConnectionEpoch => connectionEpoch;
    public bool SupportsProtectedParameters => supportsProtectedParameters;
    public bool Supports(string capability) => SupportsProtectedParameters && capabilities?.Contains(capability, StringComparer.Ordinal) == true;
    public bool Matches(OwnerKey owner) => owner.Client == client && owner.ConnectionId == connectionId && owner.Epoch == checked((long)connectionEpoch);

    public Task DispatchAsync(JobGatewayStepDispatch dispatch, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? parameters = null, DateTimeOffset expiresAt = default, RunAsCredential? credential = null)
    {
        var frame = new GatewayJobFrame
        {
            ProtocolVersion = "1.0",
            TenantId = client.TenantId,
            ClientId = client.AgentId.ToString("D"),
            ConnectionEpoch = connectionEpoch,
            ConnectionId = connectionId.ToString("D")
        };
        var step = new JobStepDispatch
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
        };
        if (parameters is null) frame.Dispatch = step;
        else
        {
            var protectedDispatch = new ProtectedJobStepDispatch { Step = step, ExpiresAtUtc = Timestamp.FromDateTimeOffset(expiresAt) };
            foreach (var (name, value) in parameters) protectedDispatch.Parameters.Add(name, value);
            protectedDispatch.Credential = credential?.Clone();
            frame.ProtectedDispatch = protectedDispatch;
        }
        return WriteAsync(frame, cancellationToken);
    }

    public Task CancelAsync(ulong jobRunId, string reason, CancellationToken cancellationToken) =>
        WriteAsync(new GatewayJobFrame
        {
            ProtocolVersion = "1.0",
            TenantId = client.TenantId,
            ClientId = client.AgentId.ToString("D"),
            ConnectionEpoch = connectionEpoch,
            ConnectionId = connectionId.ToString("D"),
            Cancel = new JobCancel { JobRunId = jobRunId, Reason = reason }
        }, cancellationToken);

    public Task PublishReceiptAsync(JobLifecycleReceipt receipt, CancellationToken cancellationToken) =>
        WriteAsync(new GatewayJobFrame
        {
            ProtocolVersion = "1.0",
            TenantId = client.TenantId,
            ClientId = client.AgentId.ToString("D"),
            ConnectionEpoch = connectionEpoch,
            ConnectionId = connectionId.ToString("D"),
            Receipt = receipt
        }, cancellationToken);

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        _outbound.Writer.TryComplete();
        _completion.Cancel();
    }

    private async Task WriteAsync(GatewayJobFrame frame, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Sequence allocation and enqueue are atomic across dispatch/cancel/receipts.
            frame.Sequence = NextSequence();
            await _outbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private ulong NextSequence() => checked((ulong)Interlocked.Increment(ref _outboundSequence));
}

internal static class ExecutionAccountServiceCapability
{
    public static string For(RunAsCredential.Types.Mode mode) => mode switch
    {
        RunAsCredential.Types.Mode.WindowsPassword => ExecutionAccountCapabilities.WindowsPassword,
        RunAsCredential.Types.Mode.UnixPrivilegedLocal => ExecutionAccountCapabilities.UnixPrivilegedLocal,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static string[] Recognized(IEnumerable<string> capabilities) => capabilities.Where(capability => capability is
        ExecutionAccountCapabilities.WindowsPassword or ExecutionAccountCapabilities.UnixPrivilegedLocal).Distinct(StringComparer.Ordinal).ToArray();

    public static void Clear(RunAsCredential? credential)
    {
        if (credential is null) return;
        credential.Password = "";
        credential.Username = "";
        credential.Domain = "";
    }
}

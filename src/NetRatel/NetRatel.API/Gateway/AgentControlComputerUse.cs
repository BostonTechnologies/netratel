using System.Collections.Concurrent;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.API.Gateway;

public sealed partial class AgentControlSessionRegistry
{
    public async Task<ComputerUseResult> RequestComputerUseAsync(ClientKey client, Guid connectionId, ulong connectionEpoch,
        ComputerUseCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(client, out var session) || !session.IsActive ||
            session.ConnectionId != connectionId || session.ConnectionEpoch != connectionEpoch ||
            !session.Capabilities.Contains(ComputerUseContract.Capability, StringComparer.Ordinal))
            throw new AgentControlSessionUnavailableException(client);

        var result = await session.RequestComputerUseAsync(command, timeout, cancellationToken).ConfigureAwait(false);
        if (!_sessions.TryGetValue(client, out var current) || !ReferenceEquals(current, session) || !session.IsActive)
            throw new OperationCanceledException("The agent control session was replaced.");
        return result;
    }
}

internal sealed partial class AgentControlSession
{
    private readonly ConcurrentDictionary<Guid, PendingComputerUse> _pendingComputerUse = new();
    private readonly object _computerUseAdmissionSync = new();

    public async Task<ComputerUseResult> RequestComputerUseAsync(ComputerUseCommand command, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (ComputerUseValidation.ValidateCommand(command) is { } code)
            return ComputerUseResult.Refused(command, code);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var payload = JsonSerializer.SerializeToUtf8Bytes(command, ComputerUseContract.JsonOptions);
        if (payload.Length > ComputerUseContract.MaximumRequestBytes)
            return ComputerUseResult.Refused(command, "request_too_large");
        var pending = new PendingComputerUse(command, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_computerUseAdmissionSync)
        {
            if (_pendingComputerUse.Count >= 8)
                return ComputerUseResult.Refused(command, "agent_busy");
            if (!_pendingComputerUse.TryAdd(command.OperationId, pending))
                return ComputerUseResult.Refused(command, "operation_already_dispatched");
        }

        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, CompletionToken);
        var dispatched = false;
        try
        {
            await WriteFrameAsync(new GatewayControlFrame
            {
                ProtocolVersion = "1.0", TenantId = client.TenantId, ClientId = client.AgentId.ToString("D"),
                ConnectionEpoch = connectionEpoch, ConnectionId = connectionId.ToString("D"),
                ComputerUseDispatch = new ComputerUseDispatch
                {
                    OperationId = command.OperationId.ToString("D"),
                    CommandJson = ByteString.CopyFrom(payload),
                    DeadlineUtc = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow().Add(timeout))
                }
            }, linked.Token).ConfigureAwait(false);
            dispatched = true;
            return await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _pendingComputerUse.TryRemove(command.OperationId, out _);
            if (dispatched && !pending.Completion.Task.IsCompletedSuccessfully && IsActive)
            {
                // The transport timeout is also an execution cancellation. It must
                // never cause an automatic replay of a possibly applied UI action.
                using var revoke = CancellationTokenSource.CreateLinkedTokenSource(CompletionToken);
                revoke.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await WriteFrameAsync(new GatewayControlFrame
                    {
                        ProtocolVersion = "1.0", TenantId = client.TenantId, ClientId = client.AgentId.ToString("D"),
                        ConnectionEpoch = connectionEpoch, ConnectionId = connectionId.ToString("D"),
                        ComputerUseCancellation = new ComputerUseCancellation { OperationId = command.OperationId.ToString("D") }
                    }, revoke.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { Complete(); }
                catch (System.Threading.Channels.ChannelClosedException) { Complete(); }
            }
        }
    }

    public bool TryCompleteComputerUse(ComputerUseCompletion completion)
    {
        if (!IsActive || !Guid.TryParse(completion.OperationId, out var operationId) ||
            !_pendingComputerUse.TryGetValue(operationId, out var pending)) return false;
        if (completion.ResultJson.Length is 0 or > ComputerUseContract.MaximumResponseBytes)
            throw new InvalidDataException("The computer-use result exceeds its transport bound.");
        ComputerUseResult? result;
        try { result = JsonSerializer.Deserialize<ComputerUseResult>(completion.ResultJson.Span, ComputerUseContract.JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("The computer-use result is invalid.", exception); }
        if (result is null || !ComputerUseValidation.IsMatchingResult(pending.Command, result))
            throw new InvalidDataException("The computer-use result does not match its operation and target.");
        return pending.Completion.TrySetResult(result);
    }

    private sealed record PendingComputerUse(ComputerUseCommand Command, TaskCompletionSource<ComputerUseResult> Completion);
}

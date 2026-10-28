using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.Client.Service.Gateway;

/// <summary>Runs bounded helper work without blocking ping, cancellation or human takeover frames.</summary>
internal sealed class AgentComputerUseDispatcher(
    Func<ComputerUseCommand, CancellationToken, Task<ComputerUseResult>>? execute,
    Func<CancellationToken, Task>? revoke,
    Func<AgentControlFrame, CancellationToken, Task> write,
    Action interruptTransport,
    CancellationToken streamToken,
    Action<string>? log = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, RunningOperation> _operations = new();
    private readonly object _admissionSync = new();
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(streamToken);

    internal void Dispatch(ComputerUseDispatch dispatch)
    {
        if (!Guid.TryParse(dispatch.OperationId, out var operationId) || operationId == Guid.Empty ||
            dispatch.CommandJson.Length is 0 or > ComputerUseContract.MaximumRequestBytes || dispatch.DeadlineUtc is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The computer-use dispatch is invalid."));
        ComputerUseCommand? command;
        try { command = JsonSerializer.Deserialize<ComputerUseCommand>(dispatch.CommandJson.Span, ComputerUseContract.JsonOptions); }
        catch (JsonException) { throw new RpcException(new Status(StatusCode.InvalidArgument, "The computer-use command is invalid.")); }
        if (command is null || command.OperationId != operationId)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The computer-use operation identity is invalid."));
        var remaining = dispatch.DeadlineUtc.ToDateTimeOffset() - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.FromSeconds(60))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The computer-use deadline is invalid."));
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cancellation.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        var operation = new RunningOperation(cancellation);
        lock (_admissionSync)
        {
            if (_operations.Count >= 8)
            {
                cancellation.Dispose();
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "The computer-use dispatch limit was exceeded."));
            }
            if (!_operations.TryAdd(operationId, operation))
            {
                cancellation.Dispose();
                throw new RpcException(new Status(StatusCode.AlreadyExists, "The computer-use operation is already running."));
            }
            operation.Task = ExecuteAsync(command, operation);
        }
    }

    internal void Cancel(ComputerUseCancellation cancellation)
    {
        if (!Guid.TryParse(cancellation.OperationId, out var operationId) || operationId == Guid.Empty)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The computer-use cancellation is invalid."));
        if (_operations.TryGetValue(operationId, out var operation)) operation.Cancel();
    }

    private async Task ExecuteAsync(ComputerUseCommand command, RunningOperation operation)
    {
        try
        {
            ComputerUseResult result;
            try
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                var invalid = ComputerUseValidation.ValidateCommand(command);
                result = invalid is not null ? ComputerUseResult.Refused(command, invalid)
                    : execute is null ? ComputerUseResult.Refused(command, "computer_use_disabled")
                    : await execute(command, operation.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new(ComputerUseContract.Version, command.OperationId, command.SessionId,
                    ComputerUseStatuses.Cancelled, "execution_cancelled", Provider: command.Provider,
                    MayHaveApplied: command.Operation == ComputerUseOperations.Act);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
            {
                result = new(ComputerUseContract.Version, command.OperationId, command.SessionId,
                    ComputerUseStatuses.Unknown, "helper_unavailable", Provider: command.Provider,
                    MayHaveApplied: command.Operation == ComputerUseOperations.Act);
            }
            if (!ComputerUseValidation.IsMatchingResult(command, result))
                result = new(ComputerUseContract.Version, command.OperationId, command.SessionId,
                    ComputerUseStatuses.Unknown, "invalid_helper_result", Provider: command.Provider,
                    MayHaveApplied: command.Operation == ComputerUseOperations.Act);
            var payload = JsonSerializer.SerializeToUtf8Bytes(result, ComputerUseContract.JsonOptions);
            if (payload.Length > ComputerUseContract.MaximumResponseBytes)
            {
                result = new(ComputerUseContract.Version, command.OperationId, command.SessionId,
                    ComputerUseStatuses.Unknown, "result_too_large", Provider: command.Provider,
                    MayHaveApplied: command.Operation == ComputerUseOperations.Act);
                payload = JsonSerializer.SerializeToUtf8Bytes(result, ComputerUseContract.JsonOptions);
            }
            await write(new AgentControlFrame
            {
                ComputerUseCompletion = new ComputerUseCompletion
                {
                    OperationId = command.OperationId.ToString("D"), ResultJson = ByteString.CopyFrom(payload)
                }
            }, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            log?.Invoke("Computer-use reply was interrupted by control-stream shutdown; leases are being revoked.");
        }
        catch (Exception)
        {
            // An unexpected provider/writer failure tears down the transport and
            // revokes every lease; it cannot silently leave background input alive.
            interruptTransport();
        }
        finally
        {
            _operations.TryRemove(command.OperationId, out _);
            operation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (revoke is not null)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await revoke(cleanup.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                log?.Invoke("Computer-use revocation did not acknowledge before the cleanup deadline.");
            }
            catch (IOException)
            {
                log?.Invoke("Computer-use helper disconnected during lease revocation.");
            }
        }
        await Task.WhenAll(_operations.Values.Select(operation => operation.Task)).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed class RunningOperation(CancellationTokenSource cancellation) : IDisposable
    {
        private readonly object _sync = new();
        private bool _disposed;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Task Task { get; set; } = Task.CompletedTask;
        internal void Cancel()
        {
            lock (_sync) if (!_disposed) Cancellation.Cancel();
        }
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                Cancellation.Dispose();
            }
        }
    }
}

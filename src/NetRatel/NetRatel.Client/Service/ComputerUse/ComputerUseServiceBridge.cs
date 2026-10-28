using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Client.Service.RemoteDesktop;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.Client.Service.ComputerUse;

/// <summary>Routes only to an existing, OS-verified interactive helper; never executes GUI work in Session 0.</summary>
internal sealed class ComputerUseServiceBridge(
    Func<RemoteDesktopUserHelperPipeHost?> getPipeHost,
    ComputerUseOptions options) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<Guid, PendingOperation> _pending = [];
    private readonly ConcurrentDictionary<Guid, ConnectedUserHelper> _usedHelpers = [];
    private readonly ConcurrentDictionary<Guid, (ConnectedUserHelper Helper, TaskCompletionSource<bool> Completion)> _revocations = [];
    private RemoteDesktopUserHelperPipeHost? _host;
    private bool _disposed;

    internal async Task<ComputerUseResult> ExecuteAsync(ComputerUseCommand command, CancellationToken cancellationToken = default)
    {
        if (_disposed) return ComputerUseResult.Refused(command, "client_stopping");
        if (!options.Enabled) return ComputerUseResult.Refused(command, "feature_disabled");
        if (!OperatingSystem.IsWindows()) return ComputerUseResult.Refused(command, "unsupported_platform");
        if (ComputerUseValidation.ValidateCommand(command) is { } invalid) return ComputerUseResult.Refused(command, invalid);
        if (command.ExpiresAtUtc <= DateTimeOffset.UtcNow) return ComputerUseResult.Refused(command, "lease_expired");
        ConnectedUserHelper? helper;
        lock (_sync)
        {
            var host = getPipeHost();
            if (host is null) return ComputerUseResult.Refused(command, "helper_unavailable");
            if (!ReferenceEquals(host, _host))
            {
                if (_host is not null) { _host.HelperMessageReceived -= Receive; _host.HelperDisconnected -= Disconnected; }
                _host = host;
                _host.HelperMessageReceived += Receive;
                _host.HelperDisconnected += Disconnected;
            }
            helper = host.GetConnectedHelper(command.Target.WindowsSessionId);
        }
        if (helper is null) return ComputerUseResult.Refused(command, "helper_unavailable", "The exact user-session helper is not connected.");
        if (!helper.ComputerUseAuthenticated) return ComputerUseResult.Refused(command, "helper_not_authenticated");
        var payload = JsonSerializer.Serialize(new ComputerUsePipeRequest(command, options), ComputerUseContract.JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > ComputerUseContract.MaximumRequestBytes)
            return ComputerUseResult.Refused(command, "request_too_large");
        var completion = new TaskCompletionSource<ComputerUseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingOperation(helper, command, completion);
        if (!_pending.TryAdd(command.OperationId, pending)) return ComputerUseResult.Refused(command, "operation_in_progress");
        _usedHelpers[helper.RouteId] = helper;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.OperationTimeout + TimeSpan.FromSeconds(5));
        try
        {
            helper.Send(new RemoteDesktopPipeMessage(ComputerUsePipeKinds.Request, payload, command.OperationId.ToString("D")), ComputerUseContract.JsonOptions);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TrySend(helper, new RemoteDesktopPipeMessage(ComputerUsePipeKinds.Cancel, null, command.OperationId.ToString("D")));
            return new(ComputerUseContract.Version, command.OperationId, command.SessionId, ComputerUseStatuses.Cancelled,
                "helper_operation_cancelled", Provider: command.Provider, MayHaveApplied: command.Operation == ComputerUseOperations.Act);
        }
        catch (IOException)
        {
            return new(ComputerUseContract.Version, command.OperationId, command.SessionId, ComputerUseStatuses.Unknown,
                "helper_disconnected", Provider: command.Provider, MayHaveApplied: command.Operation == ComputerUseOperations.Act);
        }
        finally { _pending.TryRemove(command.OperationId, out _); }
    }

    private void Receive(ConnectedUserHelper helper, RemoteDesktopPipeMessage message)
    {
        if (!Guid.TryParse(message.StreamId, out var operationId)) return;
        if (message.Kind == ComputerUsePipeKinds.Revoked && _revocations.TryGetValue(operationId, out var revocation) && revocation.Helper.RouteId == helper.RouteId)
        {
            revocation.Completion.TrySetResult(true);
            return;
        }
        if (message.Kind != ComputerUsePipeKinds.Result || !_pending.TryGetValue(operationId, out var pending) || pending.Helper.RouteId != helper.RouteId ||
            string.IsNullOrEmpty(message.PayloadJson) || message.PayloadJson.Length > ComputerUseContract.MaximumResponseBytes)
            return;
        try
        {
            var result = JsonSerializer.Deserialize<ComputerUseResult>(message.PayloadJson, ComputerUseContract.JsonOptions);
            if (result is not null && ComputerUseValidation.IsMatchingResult(pending.Command, result)) pending.Completion.TrySetResult(result);
        }
        catch (JsonException exception) { ComputerUseDiagnostics.Record("invalid_helper_result", exception); }
    }

    private void Disconnected(ConnectedUserHelper helper)
    {
        _usedHelpers.TryRemove(helper.RouteId, out _);
        foreach (var item in _pending.Values)
            if (item.Helper.RouteId == helper.RouteId)
                item.Completion.TrySetResult(new(ComputerUseContract.Version, item.Command.OperationId, item.Command.SessionId,
                    ComputerUseStatuses.Unknown, "helper_disconnected", Provider: item.Command.Provider,
                    MayHaveApplied: item.Command.Operation == ComputerUseOperations.Act));
        foreach (var item in _revocations.Values)
            if (item.Helper.RouteId == helper.RouteId) item.Completion.TrySetResult(false);
    }

    internal async Task RevokeAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var helper in _usedHelpers.Values)
        {
            var id = Guid.NewGuid();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _revocations[id] = (helper, completion);
            try
            {
                TrySend(helper, new RemoteDesktopPipeMessage(ComputerUsePipeKinds.Revoke, null, id.ToString("D")));
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                helper.Disconnect("computer_use_revoke_timeout");
            }
            finally { _revocations.TryRemove(id, out _); _usedHelpers.TryRemove(helper.RouteId, out _); }
        }
    }

    private static void TrySend(ConnectedUserHelper helper, RemoteDesktopPipeMessage message)
    {
        try { helper.Send(message, ComputerUseContract.JsonOptions); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            ComputerUseDiagnostics.Record("helper_control_send_failed", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await RevokeAllAsync().ConfigureAwait(false);
        lock (_sync)
        {
            if (_host is not null && OperatingSystem.IsWindows())
            {
                _host.HelperMessageReceived -= Receive;
                _host.HelperDisconnected -= Disconnected;
            }
            _host = null;
        }
    }

    private sealed record PendingOperation(ConnectedUserHelper Helper, ComputerUseCommand Command, TaskCompletionSource<ComputerUseResult> Completion);
}

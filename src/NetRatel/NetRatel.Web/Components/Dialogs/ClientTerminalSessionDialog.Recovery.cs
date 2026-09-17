using System.Net;
using Microsoft.JSInterop;
using NetRatel.Shared.Contracts.Terminals;

namespace NetRatel.Web.Components.Dialogs;

public partial class ClientTerminalSessionDialog
{
    private const string PresenceReplacedCode = "terminal_presence_fence_replaced";
    private const string ShellRestartNotice = "Connection refreshed. A new shell has started; previous shell state was lost.";
    private long _sessionObservationVersion;
    private bool _reconciliationRequested;
    private bool _reconciliationRunning;
    private bool _recoveryRunning;
    private bool _canReconnect;
    private bool _manualReconnectRequested;
    private bool _restartNoticePending;
    private string? _replacementAttemptedFor;
    private Task? _reconciliationTask;

    private bool IsObservedSession(string? sessionId, long version) =>
        !IsCloseRequested && !_isClosing && version == _sessionObservationVersion &&
        sessionId is not null && string.Equals(_session?.SessionId, sessionId, StringComparison.Ordinal);

    [JSInvokable]
    public Task OnGatewayTerminalBrowserActive(string sessionId, string generationText, string attachmentLeaseId)
    {
        if (ulong.TryParse(generationText, out var generation) &&
            IsCurrentGatewayTerminalAttachment(sessionId, generation, attachmentLeaseId))
        {
            QueueSessionReconciliation();
        }

        return Task.CompletedTask;
    }

    private void QueueSessionReconciliation()
    {
        if (IsCloseRequested || _isClosing)
        {
            return;
        }

        _reconciliationRequested = true;
        if (!_reconciliationRunning)
        {
            _reconciliationRunning = true;
            _reconciliationTask = InvokeAsync(ReconcileQueuedSessionAsync);
        }
    }

    private async Task ReconcileQueuedSessionAsync()
    {
        // Never join an observer from its own callback stack. Yield before
        // stopping observers, and serialize the work on the renderer context.
        await Task.Yield();
        try
        {
            while (_reconciliationRequested && !IsCloseRequested)
            {
                _reconciliationRequested = false;
                await ReconcileSessionAsync();
            }
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "[Terminal] Session recovery did not complete.");
            if (!IsCloseRequested)
            {
                _inputEnabled = false;
                _openError = "Terminal recovery did not complete. Select Reconnect to try again.";
                _canReconnect = true;
                await InvokeAsync(StateHasChanged);
            }
        }
        finally
        {
            _reconciliationRunning = false;
        }
    }

    private async Task ReconcileSessionAsync()
    {
        if (_session is not { } previous || IsCloseRequested)
        {
            return;
        }

        var version = _sessionObservationVersion;
        TerminalSessionDto? latest;
        try
        {
            latest = HasGatewayTerminalContext
                ? await Term.GetGatewaySessionAsync(previous.SessionId, _attachmentHeartbeatLifecycleCts.Token)
                : await Term.GetSessionAsync(previous.SessionId, _attachmentHeartbeatLifecycleCts.Token);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            latest = null;
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            if (IsObservedSession(previous.SessionId, version))
            {
                _inputEnabled = false;
                _canReconnect = false;
                _manualReconnectRequested = false;
                _openError = "Terminal access was denied.";
                await InvokeAsync(StateHasChanged);
            }
            return;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            if (IsObservedSession(previous.SessionId, version))
            {
                MarkTransportReconnecting();
                await InvokeAsync(StateHasChanged);
            }
            return;
        }

        if (!IsObservedSession(previous.SessionId, version))
        {
            return;
        }

        if (latest is null)
        {
            // Final tombstones expire. Explicit retry can still act on a
            // previously confirmed failed shell, but absence alone never
            // authorizes automatic creation of another session.
            if (_manualReconnectRequested && _canReconnect && HasGatewayTerminalContext &&
                string.Equals(previous.State, "failed", StringComparison.OrdinalIgnoreCase) &&
                !(previous.Generation is { } previousGeneration &&
                  IsGatewayTerminalAttachmentOwnershipLost(previous.SessionId, previousGeneration)))
            {
                _manualReconnectRequested = false;
                await ReplaceFencedSessionAsync(previous);
                return;
            }
            _manualReconnectRequested = false;
            _inputEnabled = false;
            _connectionLabel = "closed";
            _openError = "This terminal session no longer exists. Close this window and open a new terminal.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        // The browser can rotate its own lease while GET is in flight. That
        // response is stale evidence, not a takeover by a different browser.
        if (_session?.Generation != previous.Generation || _session?.AttachmentLeaseId != previous.AttachmentLeaseId)
        {
            _reconciliationRequested = true;
            return;
        }

        MergeSessionMetadata(latest);
        var ownershipLost = previous.Generation is { } generation &&
            IsGatewayTerminalAttachmentOwnershipLost(previous.SessionId, generation);
        if (ownershipLost)
        {
            _inputEnabled = false;
            _canReconnect = false;
            _manualReconnectRequested = false;
            _terminalNotice = "This terminal is now attached to another browser window.";
            await StopGatewayTerminalAttachmentHeartbeatAsync();
            await InvokeAsync(StateHasChanged);
            return;
        }

        var replace = HasGatewayTerminalContext &&
            string.Equals(latest.State, "failed", StringComparison.OrdinalIgnoreCase) &&
            ((string.Equals(latest.FailureCode, PresenceReplacedCode, StringComparison.Ordinal) &&
              _replacementAttemptedFor != latest.SessionId) || (_manualReconnectRequested && _canReconnect));
        _manualReconnectRequested = false;
        if (replace)
        {
            await ReplaceFencedSessionAsync(latest);
            return;
        }

        if (IsTerminalFinalState(latest))
        {
            if (_restartNoticePending && string.Equals(latest.State, "failed", StringComparison.OrdinalIgnoreCase))
            {
                _canReconnect = true;
                _openError = "The new shell could not open. Select Reconnect to try again.";
            }
            await StopGatewayTerminalAttachmentHeartbeatAsync();
            await RemoveGatewayTerminalHandleAsync(latest.SessionId);
            ++_sessionObservationVersion;
            using var cleanupCts = new CancellationTokenSource(CloseCleanupTimeout);
            await DisposeStreamAsync(cleanupCts.Token);
            if (string.Equals(latest.State, "closed", StringComparison.OrdinalIgnoreCase) && _console is not null)
            {
                await _console.ShowClosedAsync(latest.CloseReason);
                if (!IsCloseRequested)
                {
                    await RequestDialogCloseAsync("Terminal session closed.");
                }
                return;
            }
        }
        else
        {
            if (previous.Generation != latest.Generation || previous.AttachmentLeaseId != latest.AttachmentLeaseId)
            {
                await PersistGatewayTerminalHandleAsync(latest.SessionId, latest);
            }
            await SynchronizeGatewayTerminalAttachmentHeartbeatAsync();
            if (IsSessionInputCapable(latest))
            {
                _openError = null;
                _canReconnect = false;
                if (_restartNoticePending)
                {
                    _restartNoticePending = false;
                    _terminalNotice = ShellRestartNotice;
                    if (_console is not null)
                    {
                        await _console.AppendOutputAsync($"\r\n--- {ShellRestartNotice} ---\r\n");
                    }
                }
            }
        }
        await InvokeAsync(StateHasChanged);
    }

    private Task ReconnectAsync()
    {
        if (_canReconnect && !_recoveryRunning && !IsCloseRequested)
        {
            _manualReconnectRequested = true;
            QueueSessionReconciliation();
        }
        return Task.CompletedTask;
    }

    private async Task ReplaceFencedSessionAsync(TerminalSessionDto previous)
    {
        _replacementAttemptedFor = previous.SessionId;
        _recoveryRunning = true;
        _canReconnect = false;
        _inputEnabled = false;
        _openError = null;
        _connectionLabel = "reconnecting";
        _terminalNotice = "Connection refreshed. Starting a new shell...";
        var request = new OpenTerminalRequest(Shell, EffectiveCols, EffectiveRows, WorkingDirectory);
        ++_sessionObservationVersion;
        await InvokeAsync(StateHasChanged);
        try
        {
            using var cleanupCts = new CancellationTokenSource(CloseCleanupTimeout);
            await StopGatewayTerminalAttachmentHeartbeatAsync(cleanupCts.Token);
            await DisposeStreamAsync(cleanupCts.Token);
            if (_authoritySubscription is { } subscription)
            {
                _authoritySubscription = null;
                await subscription.DisposeAsync().AsTask().WaitAsync(cleanupCts.Token);
            }
            if (_console is not null)
            {
                await _console.DetachInputAsync();
            }
            await RemoveGatewayTerminalHandleAsync(previous.SessionId);
            if (IsCloseRequested)
            {
                return;
            }

            // One create attempt. An ambiguous HTTP failure must never be
            // retried automatically: the server might already have spawned it.
            var response = await Term.OpenGatewaySessionAsync(TenantId!.Value, AgentId!.Value, request);
            await PersistGatewayTerminalHandleAsync(response.SessionId);
            if (IsCloseRequested)
            {
                await CloseOpenedSessionAfterDialogCloseAsync(response.SessionId);
                return;
            }

            _session = previous with
            {
                SessionId = response.SessionId,
                State = "requested",
                Generation = null,
                AttachmentLeaseId = null,
                FailureCode = null,
                CloseReason = null,
                Cols = request.Cols,
                Rows = request.Rows
            };
            _hasReceivedOutput = false;
            _restartNoticePending = true;
            await StartSessionObservationAsync();
            _reconciliationRequested = true;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "[Terminal] Fresh shell open failed after presence replacement session={SessionId}", previous.SessionId);
            if (!IsCloseRequested)
            {
                _openError = "Could not start a new shell. Select Reconnect to try again.";
                _canReconnect = true;
            }
        }
        finally
        {
            _recoveryRunning = false;
            if (!IsCloseRequested)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }
}

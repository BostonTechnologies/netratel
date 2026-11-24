using Microsoft.AspNetCore.Components;
using MudBlazor;
using NetRatel.Shared.Contracts.Secrets;
using NetRatel.Shared.Contracts.Terminals;
using NetRatel.Web.Components.Shared.Secrets;
using NetRatel.Web.Services.Terminal;

namespace NetRatel.Web.Components.Dialogs;

public partial class ClientTerminalSessionDialog
{
    [Inject] private IDialogService AccountDialogs { get; set; } = default!;
    private int _consoleGeneration;
    private bool _accountSwitchRunning;
    private bool _attachAccountSessionAfterRender;

    private bool CanSwitchAccount => !_accountSwitchRunning && !IsCloseRequested &&
        _session is { Generation: > 0, AttachmentLeaseId.Length: > 0, AwaitingAccountSwitch: false } session &&
        IsSessionInputCapable(session) && !IsGatewayTerminalAttachmentOwnershipLost(session.SessionId, session.Generation.Value) &&
        HasGatewayTerminalContext;

    private async Task SwitchExecutionAccountAsync()
    {
        if (!CanSwitchAccount || _session is not { } original) return;
        var picker = await AccountDialogs.ShowAsync<TerminalAccountSwitchDialog>("Change execution context",
            new DialogParameters<TerminalAccountSwitchDialog> { { dialog => dialog.TenantId, TenantId!.Value }, { dialog => dialog.AgentId, AgentId!.Value } },
            _isFullscreen ? new DialogOptions { FullScreen = true, CloseOnEscapeKey = true } : SecretVaultPresentation.Options);
        if (await picker.Result is not { Canceled: false, Data: ExecutionIdentitySetting requested } ||
            !CanSwitchAccount || _session?.SessionId != original.SessionId) return;

        original = _session;
        _accountSwitchRunning = true;
        _terminalNotice = "Starting the new shell.";
        await InvokeAsync(StateHasChanged);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_attachmentHeartbeatLifecycleCts.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(55));
        try
        {
            var replacement = await TerminalAccountHandover.SwitchAsync(Term, original, requested, operation.Token);
            if (!IsCloseRequested && _session?.SessionId == original.SessionId)
                await AttachAccountSessionAsync(replacement);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            if (!IsCloseRequested) Snack.Add(error is OperationCanceledException ? "The terminal account switch timed out." : error.Message, Severity.Error);
        }
        finally
        {
            _accountSwitchRunning = false;
            QueueSessionReconciliation();
        }
    }

    private async Task AttachAccountSessionAsync(TerminalSessionDto next)
    {
        ++_sessionObservationVersion;
        _inputEnabled = false;
        using var cleanup = new CancellationTokenSource(CloseCleanupTimeout);
        await StopGatewayTerminalAttachmentHeartbeatAsync(cleanup.Token);
        await DisposeStreamAsync(cleanup.Token);
        if (_authoritySubscription is { } subscription)
        {
            _authoritySubscription = null;
            await subscription.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
        }
        if (IsCloseRequested) return;

        _console = null;
        ++_consoleGeneration;
        _hasReceivedOutput = false;
        _inputChannelLabel = "stdin not-started";
        _inputChannelState = "not-started";
        _session = next;
        ApplySessionLifecycle(next);
        await PersistGatewayTerminalHandleAsync(next.SessionId, next);
        _attachAccountSessionAfterRender = true;
        await InvokeAsync(StateHasChanged);
    }

    private async Task<TerminalSessionDto?> ResolveAccountHandoverAsync(TerminalSessionDto session)
    {
        var restoredId = IsTerminalFinalState(session) ? session.OriginalSessionId ?? session.ReplacementSessionId :
            session.ReplacementSessionId;
        if (restoredId is null) return null;
        try
        {
            var target = await Term.GetGatewaySessionAsync(restoredId, _attachmentHeartbeatLifecycleCts.Token);
            return target is not null && TerminalAccountHandover.IsOpen(target) ? target : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { return null; }
    }
}

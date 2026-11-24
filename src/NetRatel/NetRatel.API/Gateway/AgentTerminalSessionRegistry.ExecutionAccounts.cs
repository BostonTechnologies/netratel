using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.API.Gateway;

public sealed partial class AgentTerminalSessionRegistry
{
    private static readonly TimeSpan AccountSwitchDeadline = TimeSpan.FromSeconds(70);

    internal Task<GatewayTerminalSession> OpenWithAccountAsync(ClientKey client, string shell, string? directory,
        int columns, int rows, ExecutionIdentitySetting requested, RunAsCredential? credential, string owner, CancellationToken ct) =>
        OpenCoreAsync(client, Guid.NewGuid().ToString("N"), 1, shell, directory, columns, rows, ct, requested, credential, owner);

    internal async Task<GatewayTerminalSession> BeginAccountSwitchAsync(string sessionId, ulong generation, Guid switchId,
        ExecutionIdentitySetting requested, string owner, Func<ClientKey, CancellationToken, Task<RunAsCredential?>> resolveCredential,
        CancellationToken ct)
    {
        TerminalSession original;
        AccountSwitch handover;
        lock (_transportSync)
        {
            original = GetSession(sessionId, generation);
            if (original.AccountOwner is { } previousOwner && previousOwner != owner)
                throw new TerminalGatewayActionException("terminal_account_switch_owner", "This terminal is attached to a different operator.");
            if (original.OutgoingSwitch is { } existing && (existing.Id == switchId || existing.State != AccountSwitchState.Aborted))
            {
                if (existing.Owner == owner && existing.Requested == requested &&
                    existing.State is AccountSwitchState.Opened or AccountSwitchState.Committed &&
                    _sessions.TryGetValue(existing.Replacement, out var replacement))
                    return replacement.Snapshot();
                throw new TerminalGatewayActionException("terminal_account_switch_pending", "The terminal account switch is pending or that attempt has expired.");
            }

            if (!original.Opened || original.IncomingSwitch is { State: not AccountSwitchState.Committed } ||
                !original.SupportsIdempotentClose)
                throw new TerminalGatewayActionException("terminal_account_switch_unavailable", "An active terminal on an updated client is required to switch accounts.");

            handover = new(switchId, original, Guid.NewGuid().ToString("N"), requested, owner);
            original.OutgoingSwitch = handover;
        }

        RunAsCredential? credential = null;
        try
        {
            using var resolution = CancellationTokenSource.CreateLinkedTokenSource(ct);
            resolution.CancelAfter(TimeSpan.FromSeconds(20));
            credential = await resolveCredential(original.Client, resolution.Token);
            if (!original.Opened)
                throw new TerminalGatewayActionException("terminal_account_switch_unavailable", "The original terminal closed during the account switch.");
            var grid = original.Snapshot();
            var replacement = await OpenCoreAsync(original.Client, handover.Replacement, checked(generation + 1),
                original.ShellType, original.WorkingDirectory, grid.Columns, grid.Rows, ct, requested, credential, owner, handover);
            lock (_transportSync)
            {
                if (handover.State == AccountSwitchState.Starting) handover.State = AccountSwitchState.Opened;
            }
            _ = ExpireAccountSwitchAsync(handover, timeProvider);
            return replacement;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            handover.State = AccountSwitchState.Aborted;
            throw new TerminalGatewayActionException("terminal_account_resolution_timeout", "The execution credential did not resolve before the terminal launch deadline.");
        }
        catch
        {
            handover.State = AccountSwitchState.Aborted;
            throw;
        }
        finally { ExecutionAccountServiceCapability.Clear(credential); }
    }

    internal async Task<GatewayTerminalSession?> CommitAttachedAccountSwitchAsync(string replacementId, ulong generation, string? owner)
    {
        AccountSwitch handover;
        lock (_transportSync)
        {
            if (!_sessions.TryGetValue(replacementId, out var replacement) || replacement.Generation != generation ||
                replacement.IncomingSwitch is not { State: AccountSwitchState.Opened } pending ||
                pending.Owner != owner || !pending.Original.Opened || !replacement.Opened || !replacement.HasOutputSubscriber)
                return null;
            handover = pending;
            handover.State = AccountSwitchState.Committed;
            replacement.IncomingSwitch = null;
        }

        await CloseAccountSwitchSessionAsync(handover.Original.Id, handover.Original.Generation, "terminal_account_switched");
        return handover.Original.Snapshot();
    }

    private async Task ExpireAccountSwitchAsync(AccountSwitch handover, TimeProvider clock)
    {
        await Task.Delay(AccountSwitchDeadline, clock, CancellationToken.None);
        lock (_transportSync)
        {
            if (handover.State != AccountSwitchState.Opened) return;
            handover.State = AccountSwitchState.Aborted;
        }
        await CloseAccountSwitchSessionAsync(handover.Replacement, handover.Original.Generation + 1, "terminal_account_switch_expired");
    }

    private async Task CloseAccountSwitchSessionAsync(string sessionId, ulong generation, string reason)
    {
        try { await CloseAsync(sessionId, generation, reason, CancellationToken.None); }
        catch (TerminalGatewayActionException error)
        { logger?.LogDebug("Terminal account switch cleanup deferred. sessionId={SessionId} code={Code}", sessionId, error.Code); }
    }

    private void RejectFailedAccountSwitch(TerminalSession session)
    {
        if (session.IncomingSwitch is not { } handover) return;
        lock (_transportSync)
        {
            if (handover.State is AccountSwitchState.Starting or AccountSwitchState.Opened)
                handover.State = AccountSwitchState.Aborted;
        }
    }

    private enum AccountSwitchState { Starting, Opened, Committed, Aborted }

    private sealed class AccountSwitch(Guid id, TerminalSession original, string replacement, ExecutionIdentitySetting requested, string owner)
    {
        public Guid Id { get; } = id;
        public TerminalSession Original { get; } = original;
        public string Replacement { get; } = replacement;
        public ExecutionIdentitySetting Requested { get; } = requested;
        public string Owner { get; } = owner;
        public volatile AccountSwitchState State;
    }
}

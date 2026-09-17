using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Client.Service.RemoteDesktop;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Client.Service.RemoteSupport;

/// <summary>
/// Agent-side, exact-target preparation policy for the V2 control plane.
/// It owns no browser/media state and never selects a different helper when a
/// requested WTS target is unavailable.
/// </summary>
internal sealed class RemoteSupportV2TargetSupervisor(
    IRemoteSupportWindowsSessionSource windowsSessions,
    Func<int, ConnectedUserHelper?> getHelper,
    Func<Guid, int, ConnectedUserHelper?, CancellationToken, Task<ConnectedUserHelper?>> repairHelper,
    string serviceVersion,
    TimeProvider timeProvider,
    Func<RemoteSupportPrepareTargetCommand, CancellationToken, Task<RemoteSupportConsoleProviderReadiness?>>? prepareConsoleProvider = null)
{
    private const int InventoryLifetimeSeconds = 90;
    private ulong _inventorySequence;

    public RemoteSupportTargetInventorySnapshot CaptureInventory(int tenantId, Guid agentId)
    {
        var now = timeProvider.GetUtcNow();
        IReadOnlyList<WindowsSessionInventoryItem> entries;
        try
        {
            entries = windowsSessions.Capture();
        }
        catch
        {
            entries = Array.Empty<WindowsSessionInventoryItem>();
        }

        return new RemoteSupportTargetInventorySnapshot(
            RemoteSupportV2ContractVersions.Current,
            tenantId,
            agentId,
            checked(++_inventorySequence),
            now,
            now.AddSeconds(InventoryLifetimeSeconds),
            entries.Select(ToContract).ToArray());
    }

    public async Task<RemoteSupportPreparedTargetResult> PrepareAsync(
        RemoteSupportPrepareTargetCommand command,
        CancellationToken cancellationToken)
    {
        if (!RemoteSupportV2PreparationValidator.TryValidate(command, out var validationError))
        {
            return Rejected(command, 0, validationError!.Code, validationError.Message);
        }

        var inventory = CaptureInventory(command.TenantId, command.AgentId);
        if (command.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            return Rejected(command, inventory.InventorySequence, "prepare_expired", "The target preparation request expired before it could be evaluated.");
        }

        if (RemoteSupportV2TargetKinds.IsConsoleLogin(command.Target.Kind))
        {
            var console = inventory.Entries.SingleOrDefault(entry => entry.IsConsoleSession);
            if (console is null || console.WindowsSessionId <= 0 || !console.IsConnected)
            {
                return Rejected(command, inventory.InventorySequence, "console_target_not_found", "No active console Windows session is available.");
            }

            var readiness = prepareConsoleProvider is null
                ? null
                : await prepareConsoleProvider(command, cancellationToken).ConfigureAwait(false);
            return readiness is null || readiness.WindowsSessionId != console.WindowsSessionId ||
                readiness.ProcessId <= 0 || readiness.RouteId == Guid.Empty ||
                string.IsNullOrWhiteSpace(readiness.Version)
                ? new RemoteSupportPreparedTargetResult(
                    RemoteSupportV2ContractVersions.Current,
                    command.TenantId,
                    command.AgentId,
                    command.RequestId,
                    command.RouteNonce,
                    command.Target,
                    inventory.InventorySequence,
                    TargetValid: true,
                    ProviderReady: false,
                    Code: "console_helper_unavailable",
                    Message: "The active console target is valid, but its exact console provider is unavailable.",
                    HelperRoute: null,
                    timeProvider.GetUtcNow())
                : new RemoteSupportPreparedTargetResult(
                    RemoteSupportV2ContractVersions.Current,
                    command.TenantId,
                    command.AgentId,
                    command.RequestId,
                    command.RouteNonce,
                    command.Target,
                    inventory.InventorySequence,
                    TargetValid: true,
                    ProviderReady: true,
                    Code: "console_helper_ready",
                    Message: "The active console target and its exact console provider are ready.",
                    new RemoteSupportHelperRoute(readiness.RouteId, readiness.WindowsSessionId, "console-login", readiness.Version),
                    timeProvider.GetUtcNow());
        }

        var target = inventory.Entries.SingleOrDefault(entry =>
            entry.WindowsSessionId == command.Target.WindowsSessionId &&
            string.Equals(entry.UserSidHash, command.Target.UserSidHash, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return Rejected(command, inventory.InventorySequence, "target_session_identity_mismatch", "The selected Windows session is missing or its identity changed.");
        }

        if (!target.IsConnected || target.IsLocked || target.IsWinlogon)
        {
            return Rejected(command, inventory.InventorySequence, "target_session_not_assistable", "The selected Windows session is disconnected, locked, or at Winlogon.");
        }

        var helper = getHelper(target.WindowsSessionId);
        if (!IsCompatible(helper, target.WindowsSessionId))
        {
            helper = await repairHelper(command.RequestId, target.WindowsSessionId, helper, cancellationToken).ConfigureAwait(false);
        }

        if (!IsCompatible(helper, target.WindowsSessionId))
        {
            return new RemoteSupportPreparedTargetResult(
                RemoteSupportV2ContractVersions.Current,
                command.TenantId,
                command.AgentId,
                command.RequestId,
                command.RouteNonce,
                command.Target,
                inventory.InventorySequence,
                TargetValid: true,
                ProviderReady: false,
                Code: "target_helper_unavailable",
                Message: "The exact Windows target is valid, but its matching user-session helper is unavailable or version-mismatched.",
                HelperRoute: null,
                timeProvider.GetUtcNow());
        }

        return new RemoteSupportPreparedTargetResult(
            RemoteSupportV2ContractVersions.Current,
            command.TenantId,
            command.AgentId,
            command.RequestId,
            command.RouteNonce,
            command.Target,
            inventory.InventorySequence,
            TargetValid: true,
            ProviderReady: true,
            Code: "target_helper_ready",
            Message: "The exact Windows target and its matching helper are ready.",
            new RemoteSupportHelperRoute(helper!.RouteId, helper.SessionId, command.Target.UserSidHash!, helper.Version),
            timeProvider.GetUtcNow());
    }

    private RemoteSupportPreparedTargetResult Rejected(
        RemoteSupportPrepareTargetCommand command,
        ulong inventorySequence,
        string code,
        string message) =>
        new(
            RemoteSupportV2ContractVersions.Current,
            command.TenantId,
            command.AgentId,
            command.RequestId,
            command.RouteNonce,
            command.Target,
            inventorySequence,
            TargetValid: false,
            ProviderReady: false,
            code,
            message,
            HelperRoute: null,
            timeProvider.GetUtcNow());

    private bool IsCompatible(ConnectedUserHelper? helper, int windowsSessionId) =>
        helper is not null && helper.SessionId == windowsSessionId && RemoteSupportHelperVersion.IsCompatible(helper.Version, serviceVersion);

    private static RemoteSupportTargetInventoryEntry ToContract(WindowsSessionInventoryItem entry) =>
        new(
            entry.WindowsSessionId,
            entry.State,
            entry.UserSidHash,
            entry.IsConsoleSession,
            entry.IsConnected,
            entry.IsLocked,
            entry.IsWinlogon,
            entry.HelperConnected,
            entry.HelperVersionMatches,
            entry.HelperVersion,
            entry.DisplayLabel);
}

/// <summary>
/// Exact, ephemeral proof that the service-owned console provider is attached
/// to the current active console. It is intentionally not inventory/audit data.
/// </summary>
internal sealed record RemoteSupportConsoleProviderReadiness(
    Guid RouteId,
    int WindowsSessionId,
    int ProcessId,
    string Version);

/// <summary>Production source joining WTS inspection to the exact pipe-host helper registry.</summary>
internal sealed class GatewayRemoteSupportWindowsSessionSource(
    RemoteDesktopUserHelperPipeHost? helperPipeHost,
    string serviceVersion) : IRemoteSupportWindowsSessionSource
{
    private readonly RemoteSupportSessionIdentityCache _identityCache = new();

    public IReadOnlyList<WindowsSessionInventoryItem> Capture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<WindowsSessionInventoryItem>();
        }

#pragma warning disable CA1416
        return RemoteSupportWindowsSessionInventory.CaptureForRemoteSupport(
            helperPipeHost?.GetConnectedHelpers() ?? Array.Empty<ConnectedUserHelper>(),
            serviceVersion,
            _identityCache);
#pragma warning restore CA1416
    }
}

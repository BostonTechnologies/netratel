using System;
using System.Collections.Generic;
using System.Linq;
using NetRatel.Client.Service.RemoteDesktop;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Client.Service.RemoteSupport;

/// <summary>Supplies the current local WTS snapshot for Remote Support target preflight.</summary>
internal interface IRemoteSupportWindowsSessionSource
{
    IReadOnlyList<WindowsSessionInventoryItem> Capture();
}

/// <summary>Production source; it deliberately owns no network or persistence behaviour.</summary>
internal sealed class LocalRemoteSupportWindowsSessionSource : IRemoteSupportWindowsSessionSource
{
    public IReadOnlyList<WindowsSessionInventoryItem> Capture() =>
        RemoteSupportWindowsSessionInventory.CaptureLocalForDiagnostics();
}

/// <summary>
/// Pure target-preflight policy extracted from the provider runtime so retained
/// endpoint behaviour can be characterized with deterministic WTS snapshots.
/// </summary>
internal sealed class RemoteSupportAssistTargetPreflight(IRemoteSupportWindowsSessionSource sessions)
{
    public RemoteSupportAssistTargetPreflightResult Validate(
        OpenRemoteSupportRequest? request,
        ConnectedUserHelper? helper,
        string serviceVersion)
    {
        if (request?.TargetWindowsSessionId is null)
        {
            return RemoteSupportAssistTargetPreflightResult.Reject(
                "target_session_not_found",
                "Selected Windows session was not supplied.");
        }

        IReadOnlyList<WindowsSessionInventoryItem> snapshot;
        try
        {
            snapshot = sessions.Capture();
        }
        catch (Exception exception)
        {
            return RemoteSupportAssistTargetPreflightResult.Reject(
                "target_session_stale",
                $"Could not refresh Windows session inventory: {exception.Message}");
        }

        var target = snapshot.FirstOrDefault(item => item.WindowsSessionId == request.TargetWindowsSessionId.Value);
        if (target is null)
        {
            return RemoteSupportAssistTargetPreflightResult.Reject(
                "target_session_not_found",
                "Selected Windows session no longer exists.");
        }

        if (!string.IsNullOrWhiteSpace(request.TargetUserSidHash) &&
            !string.Equals(request.TargetUserSidHash.Trim(), target.UserSidHash, StringComparison.OrdinalIgnoreCase))
        {
            return RemoteSupportAssistTargetPreflightResult.Reject(
                "target_session_identity_mismatch",
                "Selected Windows session identity changed.");
        }

        if (!target.IsActive && !target.IsConnected)
        {
            return RemoteSupportAssistTargetPreflightResult.Reject(
                "target_session_not_assistable",
                "Selected Windows session is disconnected.");
        }

        if (target.IsLocked || target.IsWinlogon)
        {
            return RemoteSupportAssistTargetPreflightResult.Reject(
                "target_session_not_assistable",
                "Selected Windows session is locked.");
        }

        if (helper is null || helper.SessionId != request.TargetWindowsSessionId.Value)
        {
            return new RemoteSupportAssistTargetPreflightResult(
                IsTargetValid: true,
                HelperReady: false,
                HelperNeedsPreparation: true,
                Code: "target_helper_missing",
                Message: "No matching interactive helper is connected for the selected Windows session.");
        }

        if (!VersionBaseMatches(helper.Version, serviceVersion))
        {
            return new RemoteSupportAssistTargetPreflightResult(
                IsTargetValid: true,
                HelperReady: false,
                HelperNeedsPreparation: true,
                Code: "target_helper_version_mismatch",
                Message: "The matching interactive helper is stale and must be repaired before assisting this session.");
        }

        return new RemoteSupportAssistTargetPreflightResult(true, true, false, "target_helper_ready", "Selected user-session helper is ready.");
    }

    private static bool VersionBaseMatches(string? left, string? right)
    {
        static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            var plus = trimmed.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? trimmed[..plus] : trimmed;
        }

        var leftBase = Normalize(left);
        return !string.IsNullOrWhiteSpace(leftBase) &&
            string.Equals(leftBase, Normalize(right), StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record RemoteSupportAssistTargetPreflightResult(
    bool IsTargetValid,
    bool HelperReady,
    bool HelperNeedsPreparation,
    string Code,
    string Message)
{
    public static RemoteSupportAssistTargetPreflightResult Reject(string code, string message) =>
        new(false, false, false, code, message);
}

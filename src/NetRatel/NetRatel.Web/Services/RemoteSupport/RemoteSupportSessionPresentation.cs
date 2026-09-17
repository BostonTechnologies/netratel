using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Web.Services.RemoteSupport;

public static class RemoteSupportSessionPresentation
{
    public static IEnumerable<RemoteSupportWindowsSessionDto> CurrentAssistTargets(
        IEnumerable<RemoteSupportWindowsSessionDto> sessions)
    {
        var materialized = sessions.ToArray();
        if (materialized.Length == 0)
        {
            return Array.Empty<RemoteSupportWindowsSessionDto>();
        }

        var latestObservedAt = materialized.Max(x => x.ObservedUnixMs);
        var latestSequence = materialized
            .Where(x => x.ObservedUnixMs == latestObservedAt)
            .Max(x => x.InventorySequence);
        return materialized
            .Where(x => x.ObservedUnixMs == latestObservedAt && x.InventorySequence == latestSequence)
            .GroupBy(x => x.WindowsSessionId)
            .Select(group => group
                .OrderByDescending(x => x.InventorySequence)
                .ThenByDescending(x => x.ObservedUnixMs)
                .First())
            .Where(x => !x.IsLocked && !x.IsWinlogon && (x.IsConnected || x.IsActive))
            .OrderByDescending(x => x.IsConsoleSession)
            .ThenBy(x => x.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.WindowsSessionId);
    }

    public static string AssistLabel(RemoteSupportWindowsSessionDto session)
    {
        var display = string.IsNullOrWhiteSpace(session.DisplayLabel)
            ? session.Username ?? "Unknown user"
            : session.DisplayLabel;
        var type = session.IsConsoleSession
            ? "Console"
            : string.Equals(session.SessionType, "rdp", StringComparison.OrdinalIgnoreCase)
                ? "RDP"
                : string.IsNullOrWhiteSpace(session.SessionType)
                    ? "Windows"
                    : session.SessionType.ToUpperInvariant();
        var label = $"Assist: {display} — {type} session {session.WindowsSessionId}";
        return string.IsNullOrWhiteSpace(session.DisabledReason) || CanAssist(session)
            ? label
            : $"{label} ({session.DisabledReason})";
    }

    public static RemoteSupportWindowsSessionDto? ResolveFreshAssistTarget(
        RemoteSupportWindowsSessionDto selected,
        IEnumerable<RemoteSupportWindowsSessionDto> sessions)
    {
        var current = CurrentAssistTargets(sessions)
            .Where(CanAssist)
            .ToArray();
        var exact = current.FirstOrDefault(x =>
            x.WindowsSessionId == selected.WindowsSessionId &&
            string.Equals(x.UserSidHash, selected.UserSidHash, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // Rolling compatibility for 0.4.90 agents: an intermittent token query could
        // change the hash source between refreshes. Only adopt that change when both
        // the exact WTS session and normalized account still match.
        var sameSessionAndAccount = current.FirstOrDefault(x =>
            x.WindowsSessionId == selected.WindowsSessionId &&
            string.Equals(AccountKey(x), AccountKey(selected), StringComparison.Ordinal));
        if (sameSessionAndAccount is not null && AccountKey(selected) is not null)
        {
            return sameSessionAndAccount;
        }

        // A technician selected an exact Windows session. Even a unique SID on
        // another session is discovery information, not permission to move the
        // target silently. The refreshed menu must be selected again instead.
        return null;
    }

    private static bool CanAssist(RemoteSupportWindowsSessionDto session) =>
        session.IsAssistable || session.HelperLaunchable || session.HelperRepairable;

    private static string? AccountKey(RemoteSupportWindowsSessionDto session)
    {
        if (string.IsNullOrWhiteSpace(session.Username))
        {
            return null;
        }

        return $"{session.Domain?.Trim().ToLowerInvariant() ?? string.Empty}\\{session.Username.Trim().ToLowerInvariant()}";
    }
}

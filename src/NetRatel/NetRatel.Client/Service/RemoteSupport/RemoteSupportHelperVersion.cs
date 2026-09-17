using System;

namespace NetRatel.Client.Service.RemoteSupport;

/// <summary>Uses the same release-core compatibility rule for inventory and exact-target preparation.</summary>
internal static class RemoteSupportHelperVersion
{
    internal static bool IsCompatible(string? helperVersion, string? serviceVersion) =>
        TryGetReleaseCore(helperVersion, out var helperCore) &&
        TryGetReleaseCore(serviceVersion, out var serviceCore) &&
        helperCore == serviceCore;

    private static bool TryGetReleaseCore(string? value, out (int Major, int Minor, int Build) core)
    {
        core = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var release = value.Trim().Split('+', 2)[0].Split('-', 2)[0];
        if (!Version.TryParse(release, out var version)) return false;
        core = (version.Major, version.Minor, Math.Max(0, version.Build));
        return true;
    }
}

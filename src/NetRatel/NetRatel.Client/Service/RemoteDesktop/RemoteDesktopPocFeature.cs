using System;

namespace NetRatel.Client.Service.RemoteDesktop;

/// <summary>
/// The Windows remote desktop proof of concept is not production-ready.
/// Do not start its helper or advertise desktop capabilities without an explicit opt-in.
/// </summary>
internal static class RemoteDesktopPocFeature
{
    internal const string EnvironmentVariable = "NETRATEL_REMOTE_DESKTOP_POC_ENABLED";

    internal static bool IsEnabled(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value?.Trim(), "1", StringComparison.Ordinal);
}

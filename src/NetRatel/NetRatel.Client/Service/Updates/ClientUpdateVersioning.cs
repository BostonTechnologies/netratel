using System;
using System.Runtime.InteropServices;
using NuGet.Versioning;

namespace NetRatel.Client.Service.Updates;

/// <summary>
/// Version and runtime normalization shared by the Akka-native update coordinator.
/// </summary>
public static class ClientUpdateVersioning
{
    public static string NormalizePublishedVersion(string version) =>
        NuGetVersion.TryParse(version, out var parsed) ? parsed.ToNormalizedString() : version;

    public static string ResolveRuntimeId(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim().ToLowerInvariant();
        }

        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 when OperatingSystem.IsWindows() => "win-x64",
            Architecture.X64 when OperatingSystem.IsLinux() => "linux-x64",
            Architecture.Arm64 when OperatingSystem.IsWindows() => "win-arm64",
            Architecture.Arm64 when OperatingSystem.IsLinux() => "linux-arm64",
            _ => $"{RuntimeInformation.OSDescription.Trim().ToLowerInvariant()}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}"
        };
    }

    public static bool IsNewerVersion(string candidate, string current)
    {
        if (!NuGetVersion.TryParse(candidate, out var candidateVersion) ||
            !NuGetVersion.TryParse(current, out var currentVersion))
        {
            return false;
        }

        return candidateVersion > currentVersion;
    }
}

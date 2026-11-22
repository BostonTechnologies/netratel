using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NetRatel.Client.Service.Auth;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.Client.Service.Gateway;

internal static class AgentFileProtectedPaths
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly Lazy<IReadOnlyList<string>> ProtectedTrees = new(GetProtectedTrees);
    private static readonly HashSet<string> ProtectedComponents = new(PathComparer)
    {
        ".ssh", ".gnupg", ".aws", ".kube", ".azure", "agent.dat", ".netratel-credential-machine-id", "credential.key"
    };

    public static bool IsProtected(string path, bool includeAncestor = false)
    {
        if (!RemoteFilePath.TryNormalizeDirectory(path, out var normalized) ||
            OperatingSystem.IsWindows() != RemoteFilePath.IsWindows(normalized))
            return true;

        var separator = OperatingSystem.IsWindows() ? '\\' : '/';
        var components = normalized.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component => ProtectedComponents.Contains(component) ||
            component.StartsWith("agent.dat.", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            return true;

        return ProtectedTrees.Value.Any(root => RemoteFilePath.IsWithin(normalized, root) ||
            (includeAncestor && RemoteFilePath.IsWithin(root, normalized)));
    }

    private static IReadOnlyList<string> GetProtectedTrees()
    {
        var credential = AgentCredentialStore.DefaultPath();
        var roots = new List<string?> { Path.GetDirectoryName(credential) };
        if (OperatingSystem.IsWindows())
        {
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetRatel"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRatel"));
        }
        else
        {
            roots.Add(Path.GetDirectoryName(AgentCredentialStore.GetUnixProtectionKeyPath(credential)));
            roots.AddRange(["/etc", "/root", "/dev", "/proc", "/sys", "/run", "/boot", "/var/lib/netratel",
                "/var/lib/sto", "/opt/netratel", "/usr/lib/systemd", "/usr/local/lib/systemd", "/var/spool/cron"]);
        }

        if (Environment.ProcessPath is { } executable &&
            Path.GetFileNameWithoutExtension(executable).Equals("NetRatel.Client", StringComparison.OrdinalIgnoreCase))
            roots.Add(Path.GetDirectoryName(executable));

        return roots.Select(root => RemoteFilePath.TryNormalizeDirectory(root, out var normalized) ? normalized : null)
            .OfType<string>().Distinct(PathComparer).ToArray();
    }
}

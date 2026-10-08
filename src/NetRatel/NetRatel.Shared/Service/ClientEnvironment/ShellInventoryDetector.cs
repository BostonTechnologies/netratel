using System.Collections.Concurrent;
using System.Diagnostics;
using NetRatel.Shared.Service.Shells;
using ShellCapability = NetRatel.Shared.Data.ShellCapability;

namespace NetRatel.Shared.Service.ClientEnvironment;

/// <summary>Discovers executable interactive shells once for both legacy and gateway transports.</summary>
public static class ShellInventoryDetector
{
    private static readonly string[] KnownShells = ["pwsh", "powershell", "bash", "sh", "zsh", "cmd"];
    private static readonly ConcurrentDictionary<(string Path, long LastWrite), Lazy<string?>> Versions = new();

    public static IReadOnlyList<ShellCapability> Detect()
    {
        var shells = new List<ShellCapability>();

        foreach (var keyword in KnownShells)
        {
            var executable = ShellExecutableResolver.Resolve(keyword);
            if (executable is not null)
            {
                shells.Add(new ShellCapability
                {
                    Keyword = keyword,
                    Path = executable,
                    Version = Versions.GetOrAdd((executable, File.GetLastWriteTimeUtc(executable).Ticks),
                        key => new Lazy<string?>(() => TryGetVersion(key.Path, keyword))).Value
                });
            }
        }

        return shells;
    }

    public static IReadOnlyList<string> NormalizeKeywords(IEnumerable<ShellCapability> shells) =>
        shells
            .Where(shell => !string.IsNullOrWhiteSpace(shell.Path))
            .Select(shell => NormalizeKeyword(shell.Keyword))
            .Where(keyword => keyword is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string? NormalizeKeyword(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pwsh" or "powershell" or "bash" or "sh" or "zsh" or "cmd" => value.Trim().ToLowerInvariant(),
        _ => null
    };

    private static string? TryGetVersion(string path, string keyword)
    {
        var arguments = keyword switch
        {
            "cmd" => "/c ver",
            "powershell" => "-NoLogo -NoProfile -Command \"$PSVersionTable.PSVersion.ToString()\"",
            _ => "--version"
        };

        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null || !process.WaitForExit(3000))
            {
                process?.Kill(entireProcessTree: true);
                return null;
            }

            return process.StandardOutput.ReadToEnd().Trim() is { Length: > 0 } version ? version : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }
}

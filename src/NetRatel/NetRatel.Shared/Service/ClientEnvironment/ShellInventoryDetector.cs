using System.Diagnostics;
using System.Runtime.InteropServices;
using NetRatel.Shared.Data;

namespace NetRatel.Shared.Service.ClientEnvironment;

/// <summary>Discovers executable interactive shells once for both legacy and gateway transports.</summary>
public static class ShellInventoryDetector
{
    private static readonly IReadOnlyDictionary<string, string[]> KnownShells =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["pwsh"] = ["pwsh"],
            ["powershell"] = ["powershell.exe"],
            ["bash"] = ["bash"],
            ["sh"] = ["sh"],
            ["zsh"] = ["zsh"],
            ["cmd"] = ["cmd.exe"]
        };

    public static IReadOnlyList<ShellCapability> Detect()
    {
        var directories = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
        var shells = new List<ShellCapability>();

        foreach (var (keyword, executables) in KnownShells)
        {
            var executable = executables.Select(candidate => FindExecutable(candidate, directories)).FirstOrDefault(path => path is not null);
            if (executable is not null)
            {
                shells.Add(new ShellCapability
                {
                    Keyword = keyword,
                    Path = executable,
                    Version = TryGetVersion(executable, keyword)
                });
            }
        }

        if (shells.Count == 0)
        {
            AddFallbacks(shells);
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

    private static string? FindExecutable(string executable, IEnumerable<string> directories)
    {
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new[] { "", ".exe", ".cmd", ".bat" } : [""];
        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var path = Path.Combine(directory, executable + extension);
                    if (File.Exists(path)) return Path.GetFullPath(path);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry cannot make an unavailable shell appear available.
                    continue;
                }
            }
        }

        return null;
    }

    private static void AddFallbacks(ICollection<ShellCapability> shells)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var command = Environment.GetEnvironmentVariable("ComSpec");
            if (!string.IsNullOrWhiteSpace(command) && File.Exists(command))
                shells.Add(new ShellCapability { Keyword = "cmd", Path = command });

            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(powershell)) shells.Add(new ShellCapability { Keyword = "powershell", Path = powershell });
            return;
        }

        if (File.Exists("/bin/bash")) shells.Add(new ShellCapability { Keyword = "bash", Path = "/bin/bash" });
        if (File.Exists("/bin/sh")) shells.Add(new ShellCapability { Keyword = "sh", Path = "/bin/sh" });
    }

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

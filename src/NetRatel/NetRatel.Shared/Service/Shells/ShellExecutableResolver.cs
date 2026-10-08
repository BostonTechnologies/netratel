namespace NetRatel.Shared.Service.Shells;

/// <summary>Uses the same executable search for reported shells, tasks, and terminals.</summary>
public static class ShellExecutableResolver
{
    public static string? Resolve(string executable) => Resolve(executable, new SearchEnvironment(
        OperatingSystem.IsWindows(),
        Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [],
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetEnvironmentVariable("ComSpec")), IsExecutable);

    internal sealed record SearchEnvironment(
        bool IsWindows,
        IReadOnlyList<string> PathDirectories,
        string WindowsDirectory = "",
        string ProgramFiles = "",
        string ProgramFilesX86 = "",
        string? ComSpec = null);

    internal static string? Resolve(string executable, SearchEnvironment environment, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        if (Path.IsPathRooted(executable)) return exists(executable) ? executable : null;

        var keyword = executable.ToLowerInvariant() switch
        {
            "pwsh.exe" => "pwsh",
            "powershell.exe" => "powershell",
            "cmd.exe" => "cmd",
            _ => executable.ToLowerInvariant()
        };
        if (!environment.IsWindows && keyword is "powershell" or "cmd") return null;

        var name = environment.IsWindows && keyword is "pwsh" or "powershell" or "cmd"
            ? keyword + ".exe"
            : executable;
        var extensions = environment.IsWindows && !Path.HasExtension(name)
            ? new[] { "", ".exe", ".cmd", ".bat" }
            : [""];

        foreach (var directory in environment.PathDirectories)
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0) continue;
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(trimmed, name + extension);
                    if (exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (ArgumentException)
                {
                    // An invalid PATH entry must not hide an installed interpreter.
                }
            }
        }

        var fallbacks = new List<string>();
        if (environment.IsWindows)
        {
            if (keyword == "cmd" && !string.IsNullOrWhiteSpace(environment.ComSpec))
                fallbacks.Add(environment.ComSpec);
            if (!string.IsNullOrWhiteSpace(environment.WindowsDirectory))
            {
                if (keyword == "cmd") fallbacks.Add(Path.Combine(environment.WindowsDirectory, "System32", "cmd.exe"));
                if (keyword == "powershell") fallbacks.Add(Path.Combine(environment.WindowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
            }
            if (keyword == "pwsh")
            {
                foreach (var directory in new[] { environment.ProgramFiles, environment.ProgramFilesX86 })
                    if (!string.IsNullOrWhiteSpace(directory)) fallbacks.Add(Path.Combine(directory, "PowerShell", "7", "pwsh.exe"));
            }
        }
        else if (keyword is "bash" or "sh" or "zsh" or "pwsh")
        {
            foreach (var directory in new[] { "/usr/local/bin", "/usr/bin", "/bin" })
                fallbacks.Add(Path.Combine(directory, keyword));
        }

        return fallbacks.FirstOrDefault(exists);
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            return (File.GetUnixFileMode(path) &
                    (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

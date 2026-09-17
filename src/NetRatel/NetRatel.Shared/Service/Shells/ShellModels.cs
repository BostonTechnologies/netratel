namespace NetRatel.Shared.Service.Shells
{
    public sealed class ShellCapability
    {
        public string Keyword { get; set; } = "";
        public string? Path { get; set; }
        public string? Version { get; set; }
    }

    public sealed class ShellInventory
    {
        // Ensure we always have a list to mutate:
        public List<ShellCapability> Shells { get; init; } = new();

        // Case-insensitive lookup + OS fallbacks
        public ShellCapability? Find(string keyword)
        {
            var match = Shells?.FirstOrDefault(s =>
                string.Equals(s.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            // OS-specific last-resort defaults
            if (OperatingSystem.IsWindows())
            {
                if (keyword.Equals("cmd", StringComparison.OrdinalIgnoreCase))
                {
                    var comspec = Environment.GetEnvironmentVariable("ComSpec");
                    if (!string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec))
                        return new ShellCapability { Keyword = "cmd", Path = comspec };
                    // final fallback – let PATH resolve
                    return new ShellCapability { Keyword = "cmd", Path = "cmd.exe" };
                }

                if (keyword.Equals("powershell", StringComparison.OrdinalIgnoreCase))
                {
                    var ps = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
                    if (File.Exists(ps))
                        return new ShellCapability { Keyword = "powershell", Path = ps };
                }

                if (keyword.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
                {
                    var guesses = new[]
                    {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),     "PowerShell", "7", "pwsh.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PowerShell", "7", "pwsh.exe")
                    };
                    var hit = guesses.FirstOrDefault(File.Exists);
                    if (hit != null)
                        return new ShellCapability { Keyword = "pwsh", Path = hit };
                }
            }
            else
            {
                if (keyword.Equals("bash", StringComparison.OrdinalIgnoreCase))
                {
                    var bash = "/bin/bash";
                    if (File.Exists(bash))
                        return new ShellCapability { Keyword = "bash", Path = bash };
                }
                if (keyword.Equals("sh", StringComparison.OrdinalIgnoreCase))
                {
                    var sh = "/bin/sh";
                    if (File.Exists(sh))
                        return new ShellCapability { Keyword = "sh", Path = sh };
                }
            }

            return null;
        }
    }

    public static class ClientRuntime
    {
        // Singleton instance; don't replace it elsewhere
        public static ShellInventory Shells { get; } = new ShellInventory();
    }
}

using NetRatel.Shared.Contracts.Execution;

namespace NetRatel.Shared.Contracts.Jobs;

public static class JobStepExecutor
{
    public const string Automatic = "auto";
    public const string WindowsPowerShell = "windows-powershell";
    public const string PowerShell7 = "pwsh";
    public const string CommandPrompt = "cmd";
    public const string Bash = "bash";

    public static ShellExecutor Parse(string? runner) => runner?.Trim().ToLowerInvariant() switch
    {
        // The old job editor persisted "powershell" for steps that ran in pwsh.
        PowerShell7 or "powershell" or "powershellcore" => ShellExecutor.Pwsh,
        WindowsPowerShell or "powershell5" => ShellExecutor.WindowsPowerShell,
        CommandPrompt or "commandprompt" => ShellExecutor.Cmd,
        Bash => ShellExecutor.Bash,
        _ when Enum.TryParse<ShellExecutor>(runner, true, out var executor) && Enum.IsDefined(executor) => executor,
        _ => ShellExecutor.Auto
    };

    public static bool TryResolve(string? runner, ShellExecutor preference, out ShellExecutor executor)
    {
        var runnerExecutor = Parse(runner);
        executor = preference == ShellExecutor.Auto ? runnerExecutor : preference;
        return Enum.IsDefined(preference) && (runnerExecutor == ShellExecutor.Auto ||
            preference == ShellExecutor.Auto || runnerExecutor == preference);
    }

    public static string WireName(ShellExecutor executor) => executor switch
    {
        ShellExecutor.WindowsPowerShell => WindowsPowerShell,
        ShellExecutor.Pwsh => PowerShell7,
        ShellExecutor.Cmd => CommandPrompt,
        ShellExecutor.Bash => Bash,
        _ => Automatic
    };
}

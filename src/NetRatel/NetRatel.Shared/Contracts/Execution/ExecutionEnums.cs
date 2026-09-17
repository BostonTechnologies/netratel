namespace NetRatel.Shared.Contracts.Execution;

public enum ScriptType
{
    PowerShell,
    Bash,
    Python,
    JavaScript,
    TypeScript,
    Sql,
    Json
}

public enum ShellExecutor
{
    Auto,
    Pwsh,
    WindowsPowerShell,
    Bash,
    Cmd
}

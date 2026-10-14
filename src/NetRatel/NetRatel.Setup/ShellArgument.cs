namespace NetRatel.Setup;

/// <summary>Literal arguments for the displayed POSIX or Windows PowerShell command.</summary>
internal static class ShellArgument
{
    public static string Quote(string value) => "'" + (OperatingSystem.IsWindows()
        ? value.Replace("'", "''", StringComparison.Ordinal)
        : value.Replace("'", "'\\''", StringComparison.Ordinal)) + "'";
}

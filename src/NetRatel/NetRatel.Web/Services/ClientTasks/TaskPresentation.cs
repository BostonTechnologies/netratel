using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.Web.Services.ClientTasks;

internal static class TaskPresentation
{
    public static string StatusLabel(string status) => status switch
    {
        "Unknown" => "Outcome unknown",
        "TimedOut" => "Timed out",
        _ => status
    };

    public static string Title(string taskType, string? scriptName, string? commandLabel) =>
        FirstPresent(scriptName, commandLabel) ?? taskType switch
        {
            TaskKinds.ExecLibraryScript => "Library script",
            TaskKinds.ExecShellCommand => "Shell command",
            TaskKinds.OsInfo => "Operating system information",
            TaskKinds.ProcessesList => "Process list",
            TaskKinds.ProcessesTopCpu => "Top CPU processes",
            TaskKinds.DiskFree => "Disk space",
            _ => taskType
        };

    public static string Target(string? hostName, string? displayName, string? name) =>
        FirstPresent(hostName, displayName, name) ?? "Target label unavailable";

    public static string Tenant(string? name, int? id) =>
        FirstPresent(name) ?? (id.HasValue ? $"Tenant #{id.Value}" : "Tenant not recorded");

    public static bool IsTerminal(string? status) => status is not null &&
        new[] { "Completed", "Failed", "Cancelled", "TimedOut", "Unknown", "Skipped" }
            .Contains(status, StringComparer.OrdinalIgnoreCase);

    public static string Runtime(DateTimeOffset? start, DateTimeOffset? end, string status)
    {
        // Reconciliation closes the ledger; its deadline is not a measured process finish.
        if (string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase)) return "Not recorded";
        if (start is null) return MissingStart(status);
        if (end is null && IsTerminal(status)) return "Not recorded";
        var elapsed = (end ?? DateTimeOffset.UtcNow) - start.Value;
        if (elapsed < TimeSpan.Zero) return "Not recorded";
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        if (elapsed.TotalMinutes >= 1) return elapsed.ToString(@"mm\:ss");
        return $"{elapsed.TotalSeconds:0.0} s";
    }

    public static string MissingStart(string status) =>
        status is "Pending" or "Accepted" or "Skipped" ? "Not started" : "Not recorded";

    public static string ConciseReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return string.Empty;
        var firstLine = reason.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return firstLine.Length <= 120 ? firstLine : firstLine[..117] + "…";
    }

    private static string? FirstPresent(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

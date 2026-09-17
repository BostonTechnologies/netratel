using NetRatel.Web.Models.Clients;
using NetRatel.Web.Services.Telemetry;

namespace NetRatel.Web.Components.Pages.Clients;

public static class ClientPresentationFormatting
{
    public const string PendingVersion = "pending gateway hello";

    public static string OperatingSystem(ClientPresentationModel client)
    {
        var values = new[] { client.OperatingSystem, client.Architecture }
            .Where(value => !string.IsNullOrWhiteSpace(value));

        return string.Join(" · ", values) is { Length: > 0 } operatingSystem
            ? operatingSystem
            : "n/a";
    }

    public static string Latency(bool online, double? milliseconds) => milliseconds switch
    {
        not null => $"{milliseconds.Value:0} ms",
        null when online => "ping to measure",
        _ => "n/a"
    };

    public static string Version(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PendingVersion;
        }

        var buildMetadata = value.IndexOf('+');
        return buildMetadata > 0 ? value[..buildMetadata] : value;
    }

    public static string VersionTooltip(string? value) => string.IsNullOrWhiteSpace(value)
        ? PendingVersion
        : value;

    public static string Heartbeat(DateTimeOffset? value) => value is null
        ? "waiting"
        : value.Value.ToLocalTime().ToString("MM/dd/yyyy HH:mm");

    public static string TelemetryAge(DateTimeOffset? value, DateTimeOffset nowUtc) => value is null
        ? "waiting"
        : Age(nowUtc - value.Value);

    public static string Percent(double? value) => value is null ? "n/a" : $"{value.Value:0.0}%";

    public static string Memory(GatewayTelemetryMemory? memory) => memory is null
        ? "n/a"
        : $"{memory.UsedMb:0} / {memory.TotalMb:0} MB";

    public static string Network(GatewayTelemetrySummary? telemetry) => telemetry is null || telemetry.Networks.Count == 0
        ? "n/a"
        : $"↓ {Rate(telemetry.Networks.Sum(network => network.RxBytesPerSec))} / ↑ {Rate(telemetry.Networks.Sum(network => network.TxBytesPerSec))}";

    public static string Disk(GatewayTelemetryDisk? disk) => disk is null
        ? "n/a"
        : $"{disk.Scope} {Percent(disk.UsagePercent)}";

    public static string ShellLabel(string shell) => shell.ToLowerInvariant() switch
    {
        "pwsh" => "PowerShell 7",
        "powershell" => "Windows PowerShell",
        "cmd" => "Command Prompt",
        "bash" => "Bash",
        "sh" => "Shell",
        "zsh" => "Zsh",
        _ => shell
    };

    private static string Age(TimeSpan age) => age switch
    {
        var value when value < TimeSpan.FromMinutes(1) => "just now",
        var value when value < TimeSpan.FromHours(1) => $"{Math.Floor(value.TotalMinutes)}m ago",
        var value when value < TimeSpan.FromDays(1) => $"{Math.Floor(value.TotalHours)}h ago",
        var value => $"{Math.Floor(value.TotalDays)}d ago"
    };

    private static string Rate(double value) => value switch
    {
        >= 1024 * 1024 => $"{value / 1024d / 1024d:0.0} MB/s",
        >= 1024 => $"{value / 1024d:0.0} KB/s",
        _ => $"{value:0} B/s"
    };
}

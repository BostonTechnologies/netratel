using NetRatel.Shared.Contracts;
using NetRatel.Web.Services.Telemetry;

namespace NetRatel.Web.Models.Clients;

/// <summary>
/// Immutable UI snapshot composed from the authoritative Agent-ID keyed V2 read models.
/// It deliberately retains routing keys separately from display metadata.
/// </summary>
public sealed record ClientPresentationModel(
    int TenantId,
    Guid AgentId,
    string DisplayName,
    string HostName,
    string TenantName,
    string? OperatingSystem,
    string? Architecture,
    bool Online,
    bool Enabled,
    DateTimeOffset? LastHeartbeatUtc,
    string? AgentVersion,
    IReadOnlyList<string> Capabilities,
    GatewayTerminalCapabilityDto? Terminal,
    GatewayTelemetrySummary? Telemetry,
    GatewayFileCapabilityDto? File = null)
{
    public bool CanUseTerminal =>
        Online && Capabilities.Contains("terminal-gateway", StringComparer.OrdinalIgnoreCase) &&
        Terminal is { Supported: true, TransportReady: true, AvailableShells.Count: > 0 };

    public bool CanUseFilesystem => File is null
        ? Online && Capabilities.Contains("file-gateway", StringComparer.OrdinalIgnoreCase)
        : Online && File is { Configured: true, Advertised: true, SessionActive: true, FenceMatchesPresence: true };

    public string FilesystemUnavailableReason => !Online
        ? "Client is offline"
        : File?.ReadinessReason switch
        {
            "file_gateway_not_advertised" => "File gateway is not advertised by the client",
            "file_gateway_not_admitted" => "File gateway is advertised but not admitted",
            "file_gateway_fenced" => "File gateway session is fenced by presence",
            "file_gateway_presence_offline" => "Client presence is offline",
            null when CanUseFilesystem => "Browse files",
            _ => "File gateway is unavailable"
        };

    public bool CanUseLogs => Online && Capabilities.Contains("log-gateway", StringComparer.OrdinalIgnoreCase);

    public string LogsUnavailableReason => !Online
        ? "Offline"
        : !Capabilities.Contains("log-gateway", StringComparer.OrdinalIgnoreCase)
            ? "Client upgrade required"
            : string.Empty;

    public bool CanUseRemoteSupport => Online && Capabilities.Contains("remote-support-gateway", StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> AvailableShells => Terminal?.AvailableShells
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(GetShellOrder)
        .ThenBy(shell => shell, StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    private static int GetShellOrder(string shell) => shell.ToLowerInvariant() switch
    {
        "pwsh" => 0,
        "powershell" => 1,
        "bash" => 2,
        "sh" => 3,
        "zsh" => 4,
        "cmd" => 5,
        _ => int.MaxValue
    };
}

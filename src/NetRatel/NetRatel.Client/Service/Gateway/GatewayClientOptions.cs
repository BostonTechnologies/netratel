namespace NetRatel.Client.Service.Gateway;

public sealed class GatewayClientOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ProtocolVersion { get; set; } = "1.0";

    public bool ControlGatewayEnabled { get; set; }

    /// <summary>Enables the separate byte-bearing AgentFileGateway stream.</summary>
    public bool FileGatewayEnabled { get; set; }

    /// <summary>Enables the separate, bounded structured runtime-log stream.</summary>
    public bool LogGatewayEnabled { get; set; } = true;

    /// <summary>Enables the separate remote-support signalling stream.</summary>
    public bool RemoteSupportGatewayEnabled { get; set; }

    /// <summary>
    /// Enables the additive, agent-authenticated V2 WTS inventory and exact
    /// target-preparation stream. It does not enable browser media or replace
    /// the retained signalling gateway.
    /// </summary>
    public bool RemoteSupportV2InventoryEnabled { get; set; }

    /// <summary>
    /// Enables the RS2-4 typed direct-media adapter after exact-target
    /// preparation and the replica-safe edge have been enabled server-side.
    /// </summary>
    public bool RemoteSupportV2MediaEnabled { get; set; }

    /// <summary>Enables the fenced terminal PTY stream for the active presence session.</summary>
    public bool TerminalGatewayEnabled { get; set; }

    public int TelemetryFastIntervalSeconds { get; set; } = 5;

    public int TelemetrySlowIntervalSeconds { get; set; } = 30;

    public int TelemetryInteractiveIntervalMilliseconds { get; set; } = 1000;

    public int TelemetryMinimumIntervalMilliseconds { get; set; } = 1000;

    public int TelemetryPolicyMaximumLifetimeSeconds { get; set; } = 120;
}

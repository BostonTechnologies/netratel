namespace NetRatel.Client.Service.Gateway;

public sealed class GatewayClientOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ProtocolVersion { get; set; } = "1.0";

    public int TelemetryFastIntervalSeconds { get; set; } = 5;

    public int TelemetrySlowIntervalSeconds { get; set; } = 30;

    public int TelemetryInteractiveIntervalMilliseconds { get; set; } = 1000;

    public int TelemetryMinimumIntervalMilliseconds { get; set; } = 1000;

    public int TelemetryPolicyMaximumLifetimeSeconds { get; set; } = 120;
}

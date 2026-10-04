namespace NetRatel.Client.Service.Gateway;

public sealed class GatewayClientOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ProtocolVersion { get; set; } = "1.0";

    // No server policy exists until admission. Bound the whole bootstrap exchange.
    public int PresenceBootstrapTimeoutSeconds { get; set; } = 15;

    public int PresenceTeardownTimeoutSeconds { get; set; } = 5;

    // A stream failing at the known one-minute ingress boundary is still flapping.
    public int PresenceStabilityThresholdSeconds { get; set; } = 120;

    public int TelemetryFastIntervalSeconds { get; set; } = 5;

    public int TelemetrySlowIntervalSeconds { get; set; } = 30;

    public int TelemetryInteractiveIntervalMilliseconds { get; set; } = 1000;

    public int TelemetryMinimumIntervalMilliseconds { get; set; } = 1000;

    public int TelemetryPolicyMaximumLifetimeSeconds { get; set; } = 120;
}

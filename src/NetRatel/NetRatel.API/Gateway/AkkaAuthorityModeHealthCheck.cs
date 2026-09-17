using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Akka.Configuration;

namespace NetRatel.API.Gateway;

public sealed class AkkaAuthorityModeHealthCheck(NetRatelAkkaMigrationOptions options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["authorityMode"] = options.AuthorityModeName,
            ["presenceAuthority"] = options.PresenceAuthority,
            ["pingAuthority"] = options.IsPingAuthorityActive ? options.PresenceAuthority : "unavailable",
            ["telemetryAuthority"] = options.IsTelemetryAuthorityActive ? options.PresenceAuthority : "unavailable",
            ["fileBrowseAuthority"] = options.IsFileBrowseAuthorityActive ? options.PresenceAuthority : "unavailable",
            ["remoteSupportAuthority"] = options.IsRemoteSupportAuthorityActive ? options.PresenceAuthority : "unavailable",
            ["commandAuthority"] = options.IsCommandAuthorityActive ? options.PresenceAuthority : "unavailable",
            ["jobAuthority"] = options.IsJobAuthorityActive ? options.PresenceAuthority : "unavailable",
            ["terminalAuthority"] = options.IsTerminalAuthorityActive ? options.PresenceAuthority : "unavailable",
            ["signalRAuthority"] = options.IsSignalRAuthorityActive ? options.PresenceAuthority : "unavailable",
            ["presenceAuthorityEnabled"] = options.PresenceAuthorityEnabled,
            ["pingAuthorityEnabled"] = options.PingAuthorityEnabled,
            ["telemetryAuthorityEnabled"] = options.TelemetryAuthorityEnabled,
            ["fileBrowseAuthorityEnabled"] = options.FileBrowseAuthorityEnabled,
            ["remoteSupportAuthorityEnabled"] = options.RemoteSupportAuthorityEnabled,
            ["commandAuthorityEnabled"] = options.CommandAuthorityEnabled,
            ["jobAuthorityEnabled"] = options.JobAuthorityEnabled,
            ["terminalAuthorityEnabled"] = options.TerminalAuthorityEnabled,
            ["signalRAuthorityEnabled"] = options.SignalRAuthorityEnabled,
            ["activeAuthorityPaths"] = GetActiveAuthorityPaths(options)
        };

        return Task.FromResult(HealthCheckResult.Healthy(
            options.IsRemoteSupportAuthorityActive || options.IsFileBrowseAuthorityActive || options.IsTelemetryAuthorityActive || options.IsCommandAuthorityActive || options.IsJobAuthorityActive || options.IsTerminalAuthorityActive || options.IsSignalRAuthorityActive
                ? $"Akka authority is active for {GetActiveAuthorityPaths(options)}."
                : options.IsPingAuthorityActive
                ? "Akka authority is active for presence and ping."
                : options.IsPresenceAuthorityActive
                    ? "Akka authority is active for presence only."
                : "Akka authority is inactive; no operational authority is configured.",
            data));
    }

    private static string GetActiveAuthorityPaths(NetRatelAkkaMigrationOptions options)
    {
        var active = new List<string>();
        if (options.IsPresenceAuthorityActive) active.Add("presence");
        if (options.IsPingAuthorityActive) active.Add("ping");
        if (options.IsTelemetryAuthorityActive) active.Add("telemetry");
        if (options.IsFileBrowseAuthorityActive) active.Add("file-browser");
        if (options.IsCommandAuthorityActive) active.Add("commands");
        if (options.IsJobAuthorityActive) active.Add("jobs");
        if (options.IsRemoteSupportAuthorityActive) active.Add("remote-support");
        if (options.IsTerminalAuthorityActive) active.Add("terminal");
        if (options.IsSignalRAuthorityActive) active.Add("signalr");
        return active.Count == 0 ? "none" : string.Join(',', active);
    }
}

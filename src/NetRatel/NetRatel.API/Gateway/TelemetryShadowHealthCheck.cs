using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Application.Telemetry;

namespace NetRatel.API.Gateway;

public sealed class TelemetryShadowHealthCheck(IClientTelemetryRouter telemetryRouter) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await telemetryRouter.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                "The shadow telemetry actor route is available.",
                new Dictionary<string, object>
                {
                    ["mode"] = status.Mode,
                    ["activeTelemetryClients"] = status.ActiveTelemetryClients,
                    ["acceptedCount"] = status.AcceptedCount,
                    ["rejectedCount"] = status.RejectedCount,
                    ["lastUpdateTimestamp"] = status.LastUpdateTimestamp?.ToString("O") ?? "never",
                    ["telemetryAuthority"] = status.Authority
                });
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "The shadow telemetry actor route is unavailable.",
                exception);
        }
    }
}

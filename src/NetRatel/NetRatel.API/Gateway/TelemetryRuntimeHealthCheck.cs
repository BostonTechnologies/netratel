using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Application.Telemetry;

namespace NetRatel.API.Gateway;

public sealed class TelemetryRuntimeHealthCheck(IClientTelemetryRouter telemetryRouter) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await telemetryRouter.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                "The Akka telemetry route is available.",
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "The Akka telemetry route is unavailable.",
                exception);
        }
    }
}

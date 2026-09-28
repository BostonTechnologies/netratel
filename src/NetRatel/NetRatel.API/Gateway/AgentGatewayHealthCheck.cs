using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

public sealed class AgentGatewayHealthCheck(
    IClientPresenceRouter presenceRouter) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await presenceRouter.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                "The Akka presence route is available.",
                new Dictionary<string, object>
                {
                    ["mode"] = status.Mode,
                    ["activeClientActors"] = status.ActiveClientActors
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "The Akka presence route is unavailable.",
                exception);
        }
    }
}

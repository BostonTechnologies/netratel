using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Application.Commands;

namespace NetRatel.API.Gateway;

public sealed class CommandShadowHealthCheck(IClientCommandRouter commandRouter) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await commandRouter.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                "The shadow command actor route is available.",
                new Dictionary<string, object>
                {
                    ["mode"] = status.Mode,
                    ["activeCommands"] = status.ActiveCommands,
                    ["completedCommands"] = status.CompletedCommands,
                    ["failedCommands"] = status.FailedCommands,
                    ["invalidTransitions"] = status.InvalidTransitions,
                    ["staleEvents"] = status.StaleEvents,
                    ["commandAuthority"] = status.Authority
                });
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "The shadow command actor route is unavailable.",
                exception);
        }
    }
}

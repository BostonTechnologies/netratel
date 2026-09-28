using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Application.Commands;

namespace NetRatel.API.Gateway;

public sealed class CommandRuntimeHealthCheck(IClientCommandRouter commandRouter) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await commandRouter.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                "The Akka command lifecycle route is available.",
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "The Akka command lifecycle route is unavailable.",
                exception);
        }
    }
}

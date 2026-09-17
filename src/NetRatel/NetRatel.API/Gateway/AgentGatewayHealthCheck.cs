using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

public sealed class AgentGatewayHealthCheck(
    IClientPresenceRouter presenceRouter,
    NetRatelAkkaMigrationOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await presenceRouter.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                options.IsPresenceAuthorityActive
                    ? "The Akka presence authority route is available."
                    : "The shadow presence actor route is available.",
                new Dictionary<string, object>
                {
                    ["mode"] = status.Mode,
                    ["activeClientActors"] = status.ActiveClientActors,
                    ["authorityMode"] = options.AuthorityModeName,
                    ["presenceAuthority"] = options.PresenceAuthority,
                    ["operationalPresenceAuthority"] = options.OperationalPresenceAuthority,
                    ["presenceAuthorityEnabled"] = options.PresenceAuthorityEnabled
                });
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                options.IsPresenceAuthorityActive
                    ? "The Akka presence authority route is unavailable."
                    : "The shadow presence actor route is unavailable.",
                exception,
                new Dictionary<string, object>
                {
                    ["authorityMode"] = options.AuthorityModeName,
                    ["presenceAuthority"] = options.PresenceAuthority,
                    ["operationalPresenceAuthority"] = options.OperationalPresenceAuthority,
                    ["presenceAuthorityEnabled"] = options.PresenceAuthorityEnabled
                });
        }
    }
}

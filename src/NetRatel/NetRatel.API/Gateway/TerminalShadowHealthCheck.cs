using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.API.Services.Terminal;
using NetRatel.Application.Terminals;

namespace NetRatel.API.Gateway;

public sealed class TerminalShadowHealthCheck(
    ITerminalShadowRouter router,
    ITerminalShadowObservationSink ingress) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var route = await router.ProbeAsync(cancellationToken).ConfigureAwait(false);
            var ingressStatus = ingress.GetStatus();
            return HealthCheckResult.Healthy(
                "The local terminal metadata shadow is available.",
                new Dictionary<string, object>
                {
                    ["activeTerminalSessions"] = route.ActiveTerminalSessions,
                    ["closedTerminalSessions"] = route.ClosedTerminalSessions,
                    ["failedTerminalSessions"] = route.FailedTerminalSessions,
                    ["sessionActorCount"] = route.SessionActorCount,
                    ["rejectedStaleEvents"] = route.RejectedStaleEvents,
                    ["invalidTransitions"] = route.InvalidTransitions,
                    ["droppedMetadataEvents"] = route.DroppedMetadataEvents,
                    ["coalescedMetadataEvents"] = route.CoalescedMetadataEvents,
                    ["retainedMetadataCount"] = route.RetainedMetadataCount,
                    ["maximumObservedSequence"] = route.MaximumObservedSequence,
                    ["requestedSessions"] = route.LifecycleCounts.Requested,
                    ["openedSessions"] = route.LifecycleCounts.Opened,
                    ["closedSessions"] = route.LifecycleCounts.Closed,
                    ["failedSessions"] = route.LifecycleCounts.Failed,
                    ["ingressDropped"] = ingressStatus.Dropped,
                    ["mode"] = route.Mode,
                    ["terminalAuthority"] = route.Authority
                });
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "The local terminal metadata shadow is unavailable.",
                exception);
        }
    }
}

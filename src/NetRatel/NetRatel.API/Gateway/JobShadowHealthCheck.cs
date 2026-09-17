using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;

namespace NetRatel.API.Gateway;

public sealed class JobShadowHealthCheck(
    IJobShadowRouter router,
    IJobShadowPersistenceStore persistenceStore,
    IJobShadowObservationSink ingress) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var route = await router.ProbeAsync(cancellationToken).ConfigureAwait(false);
            var persistence = await persistenceStore.GetDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
            var ingressStatus = ingress.GetStatus();
            return HealthCheckResult.Healthy(
                "The local job shadow observer is available.",
                new Dictionary<string, object>
                {
                    ["activeShadowJobs"] = route.ActiveShadowJobs,
                    ["completedShadowJobs"] = route.CompletedShadowJobs,
                    ["failedShadowJobs"] = route.FailedShadowJobs,
                    ["activeJobSteps"] = route.ActiveJobSteps,
                    ["invalidTransitions"] = route.InvalidTransitions,
                    ["duplicateEvents"] = route.DuplicateEvents,
                    ["staleEvents"] = route.StaleEvents,
                    ["missingCommandCorrelations"] = persistence.MissingCommandCorrelationCount,
                    ["replayCount"] = persistence.ReplayCount,
                    ["recoverySuccessCount"] = persistence.RecoverySuccessCount,
                    ["observationCount"] = persistence.ObservationCount,
                    ["ingressDropped"] = ingressStatus.Dropped,
                    ["persistenceFailures"] = ingressStatus.PersistenceFailures,
                    ["lastPersistedAtUtc"] = persistence.LastPersistedAtUtc?.ToString("O") ?? "never",
                    ["lastReplayAtUtc"] = persistence.LastReplayAtUtc?.ToString("O") ?? "never",
                    ["mode"] = route.Mode,
                    ["jobAuthority"] = route.Authority
                });
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "The local job shadow observer is unavailable.",
                exception);
        }
    }
}

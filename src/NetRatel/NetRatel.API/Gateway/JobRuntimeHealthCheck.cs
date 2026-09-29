using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Application.Jobs;

namespace NetRatel.API.Gateway;

public sealed class JobRuntimeHealthCheck(
    IJobRuntimeRouter jobRouter,
    IJobObservationStore persistenceStore) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var route = await jobRouter.ProbeAsync(cancellationToken).ConfigureAwait(false);
            var persistence = await persistenceStore.GetDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                "The Akka job lifecycle and durable observation store are available.",
                new Dictionary<string, object>
                {
                    ["activeJobs"] = route.ActiveJobs,
                    ["completedJobs"] = route.CompletedJobs,
                    ["failedJobs"] = route.FailedJobs,
                    ["activeSteps"] = route.ActiveJobSteps,
                    ["invalidTransitions"] = route.InvalidTransitions,
                    ["duplicateEvents"] = route.DuplicateEvents,
                    ["staleEvents"] = route.StaleEvents,
                    ["missingCommandCorrelations"] = persistence.MissingCommandCorrelationCount,
                    ["replayCount"] = persistence.ReplayCount,
                    ["recoverySuccessCount"] = persistence.RecoverySuccessCount,
                    ["observationCount"] = persistence.ObservationCount,
                    ["lastPersistedAtUtc"] = persistence.LastPersistedAtUtc?.ToString("O") ?? "never",
                    ["lastReplayAtUtc"] = persistence.LastReplayAtUtc?.ToString("O") ?? "never",
                    ["mode"] = route.Mode,
                    ["jobAuthority"] = route.Authority
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "The Akka job lifecycle or durable observation store is unavailable.",
                exception);
        }
    }
}

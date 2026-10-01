using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Akka.Observability;
using NetRatel.Application.Commands;

namespace NetRatel.API.Gateway;

public sealed class CommandLifecyclePersistenceHealthCheck(ICommandPersistenceStore persistenceStore) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var diagnostics = await persistenceStore.GetDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
            NetRatelAkkaTelemetry.SetCommandPersistenceDepths(
                diagnostics.InboxDepth,
                diagnostics.OutboxDepth);
            return HealthCheckResult.Healthy(
                "The command lifecycle persistence store is available.",
                new Dictionary<string, object>
                {
                    ["inboxDepth"] = diagnostics.InboxDepth,
                    ["outboxDepth"] = diagnostics.OutboxDepth,
                    ["replayCount"] = diagnostics.ReplayCount,
                    ["duplicateDetectionCount"] = diagnostics.DuplicateDetectionCount,
                    ["recoverySuccessCount"] = diagnostics.RecoverySuccessCount,
                    ["lastReplayAtUtc"] = diagnostics.LastReplayAtUtc?.ToString("O") ?? "never",
                    ["mode"] = diagnostics.Mode,
                    ["commandAuthority"] = diagnostics.Authority
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "The command lifecycle persistence store is unavailable.",
                exception);
        }
    }
}

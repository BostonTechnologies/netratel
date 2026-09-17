using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Application.Commands;
using NetRatel.Akka.Observability;

namespace NetRatel.API.Gateway;

public sealed class CommandPersistenceHealthCheck(ICommandPersistenceStore persistenceStore) : IHealthCheck
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
                "The shadow command persistence foundation is available.",
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
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "The shadow command persistence foundation is unavailable.",
                exception);
        }
    }
}

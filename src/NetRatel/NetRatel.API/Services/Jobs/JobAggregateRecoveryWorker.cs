using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services.Jobs;

/// <summary>Rediscovers durable parent queues after restart. Claimed children are never started again.</summary>
public sealed class JobAggregateRecoveryWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<JobAggregateRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var after = 0L;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var wakeup = scope.ServiceProvider.GetRequiredService<IJobAggregateWakeup>();
                var parents = await db.Set<JobAggregateControlRecord>().AsNoTracking()
                    .Where(control => control.ParentRunId > after && control.TerminalReadyAtUtc == null)
                    .OrderBy(control => control.ParentRunId).Take(16).Select(control => control.ParentRunId).ToArrayAsync(stoppingToken);
                foreach (var parent in parents) await wakeup.WakeAsync(checked((ulong)parent), stoppingToken);
                after = parents.Length == 0 ? 0 : parents[^1];
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Aggregate queue recovery failed: {FailureType}.", error.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

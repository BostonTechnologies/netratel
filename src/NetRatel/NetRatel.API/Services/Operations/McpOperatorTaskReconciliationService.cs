using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Commands;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services.Operations;

/// <summary>Repairs task projections only from complete, authoritative command lifecycle evidence.</summary>
public sealed class McpOperatorTaskReconciliationService(
    OrchestratorDbContext db,
    ICommandPersistenceStore commands,
    IMcpOperatorTaskStore tasks,
    ILogger<McpOperatorTaskReconciliationService> logger)
{
    public const int MaximumCandidates = 50;
    public const int MaximumHistoryEvents = 8;
    public const string ResultUnavailable = "Result payload unavailable; terminal status recovered from authoritative command lifecycle.";
    public static readonly TimeSpan MinimumProjectionAge = TimeSpan.FromSeconds(30);

    public async Task<McpOperatorTaskReconciliationBatch> ReconcileAsync(
        DateTimeOffset now, long afterTaskId = 0, CancellationToken cancellationToken = default)
    {
        if (afterTaskId < 0) throw new ArgumentOutOfRangeException(nameof(afterTaskId));
        var cutoff = now - MinimumProjectionAge;
        var candidates = await db.McpOperatorTasks.AsNoTracking()
            .Where(task => task.TaskActivityId > afterTaskId && task.UpdatedAtUtc <= cutoff &&
                task.State != "Completed" && task.State != "Failed" && task.State != "Cancelled")
            .OrderBy(task => task.TaskActivityId).Take(MaximumCandidates)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var reconciled = 0;
        foreach (var task in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = new CommandKey(task.TenantId, task.CommandId);
            if (!command.IsValid)
                continue;
            var history = await commands.ReplayBoundedAsync(command,
                MaximumHistoryEvents, cancellationToken).ConfigureAwait(false);
            if (!TryGetTerminal(task, history, out var terminal) || terminal!.StatusTimestamp > cutoff)
                continue;

            // The store re-reads the current version and never overwrites an
            // already terminal task, including a concurrent gateway completion.
            await tasks.RecordLifecycleAsync(task.CommandId, task.TenantId, task.AgentId,
                terminal!.Status.ToString(), ResultUnavailable, terminal.StatusTimestamp, cancellationToken).ConfigureAwait(false);
            var current = await db.McpOperatorTasks.AsNoTracking().SingleOrDefaultAsync(
                row => row.Id == task.Id, cancellationToken).ConfigureAwait(false);
            if (current?.State != terminal.Status.ToString() || current.CompletedAtUtc != terminal.StatusTimestamp ||
                current.ResultSummary != ResultUnavailable)
                continue;
            reconciled++;
            logger.LogInformation("Recovered MCP task {TaskId} from authoritative command lifecycle {State}.",
                task.TaskActivityId, current.State);
        }
        var next = candidates.Count == MaximumCandidates ? candidates[^1].TaskActivityId : 0;
        return new(candidates.Count, reconciled, next);
    }

    private static bool TryGetTerminal(McpOperatorTaskRecord task, CommandPersistenceReplayPage page,
        out CommandLifecycleEvent? terminal)
    {
        terminal = null;
        if (!page.IsComplete || page.Events.Count is 0 or > MaximumHistoryEvents)
            return false;
        CommandLifecycleEvent? previous = null;
        var requestedAt = page.Events[0].RequestTimestamp;
        if (requestedAt < task.CreatedAtUtc)
            return false;
        foreach (var item in page.Events)
        {
            if (!item.IsAuthoritative || item.Client.TenantId != task.TenantId || item.Client.AgentId != task.AgentId ||
                !string.Equals(item.CommandId, task.CommandId, StringComparison.Ordinal) ||
                !string.Equals(item.CorrelationId, task.CorrelationId, StringComparison.Ordinal) ||
                item.RequestTimestamp != requestedAt || item.StatusTimestamp < requestedAt ||
                item.Version == 0 || item.Sequence == 0 ||
                (previous is not null && (item.Version <= previous.Version || item.Sequence <= previous.Sequence ||
                    item.StatusTimestamp < previous.StatusTimestamp)) ||
                !CommandLifecycleRules.IsValidTransition(previous?.Status, item.Status))
                return false;
            previous = item;
        }
        if (previous is null || !CommandLifecycleRules.IsTerminal(previous.Status))
            return false;
        terminal = previous;
        return true;
    }
}

public sealed record McpOperatorTaskReconciliationBatch(int Scanned, int Reconciled, long NextAfterTaskId);

/// <summary>Visits bounded pages so old unrecoverable rows cannot starve later tasks.</summary>
public sealed class McpOperatorTaskReconciliationHostedService(
    IServiceScopeFactory scopeFactory, TimeProvider timeProvider,
    ILogger<McpOperatorTaskReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        long cursor = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<McpOperatorTaskReconciliationService>()
                    .ReconcileAsync(timeProvider.GetUtcNow(), cursor, deadline.Token).ConfigureAwait(false);
                cursor = result.NextAfterTaskId;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                logger.LogWarning("The bounded MCP task reconciliation cycle timed out.");
            }
            catch (Exception exception) when (exception is DbException or DbUpdateException)
            {
                logger.LogWarning(exception, "The MCP task reconciliation database operation failed.");
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                return;
        }
    }
}

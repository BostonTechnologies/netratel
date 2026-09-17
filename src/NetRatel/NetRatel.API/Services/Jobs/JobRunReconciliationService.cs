using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Jobs;
using NetRatel.Shared.Data.Task;

namespace NetRatel.API.Services.Jobs;

public sealed class JobRunReconciliationService
{
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(5);

    private readonly IJobRunService _jobRuns;
    private readonly ILogger<JobRunReconciliationService> _logger;

    public JobRunReconciliationService(
        IJobRunService jobRuns,
        ILogger<JobRunReconciliationService> logger)
    {
        _jobRuns = jobRuns;
        _logger = logger;
    }

    public async Task<int> ReconcileAsync(DateTimeOffset now, TimeSpan? staleAfter = null, CancellationToken ct = default)
    {
        var cutoff = now - (staleAfter ?? DefaultStaleAfter);
        var runs = await _jobRuns.ListAsync(ct).ConfigureAwait(false);
        var reconciled = 0;

        foreach (var run in runs)
        {
            ct.ThrowIfCancellationRequested();

            var effectiveStart = run.StartedAtUtc ?? run.CreatedAtUtc;
            if (effectiveStart > cutoff)
            {
                continue;
            }

            var details = await _jobRuns.GetDetailsAsync(run.Id, ct).ConfigureAwait(false);
            if (details is null)
            {
                continue;
            }

            var message = $"Job run {run.Id} timed out waiting for client task completion after {(staleAfter ?? DefaultStaleAfter).TotalMinutes:0} minutes.";
            var changed = await ReconcileRunAsync(details, now, message, ct).ConfigureAwait(false);
            if (changed)
            {
                reconciled++;
            }
        }

        if (reconciled > 0)
        {
            _logger.LogWarning("Reconciled {Count} stale job run(s).", reconciled);
        }

        return reconciled;
    }

    private async Task<bool> ReconcileRunAsync(JobRunDetails details, DateTimeOffset now, string message, CancellationToken ct)
    {
        var changed = false;
        foreach (var activity in details.Activities)
        {
            if (!IsTerminalTaskStatus(activity.Status))
            {
                await _jobRuns.UpdateTaskActivityStatusAsync(
                    new UpdateJobTaskActivityStatusCommand(
                        activity.RequestId,
                        "TimedOut",
                        message,
                        now),
                    ct).ConfigureAwait(false);

                changed = true;
            }

        }

        if (IsTerminal(details.Run.Status))
        {
            return changed;
        }

        foreach (var step in details.Steps.Where(x => !IsTerminal(x.Status)))
        {
            if (!step.JobStepId.HasValue)
            {
                continue;
            }

            await _jobRuns.UpsertStepRunAsync(
                new UpsertJobStepRunCommand(
                    step.Id,
                    step.JobRunId,
                    step.JobStepId.Value,
                    JobStepRunState.Failed,
                    step.Ordinal,
                    step.TaskRequestId,
                    message,
                    step.StartedAtUtc,
                    now),
                ct).ConfigureAwait(false);
            changed = true;
        }

        var run = details.Run;
        await _jobRuns.UpsertRunAsync(
            new UpsertJobRunCommand(
                run.Id,
                run.JobId,
                run.TenantId,
                run.ClientIdentity,
                run.StartedBy,
                JobRunState.TimedOut,
                run.CurrentStepOrdinal,
                run.CreatedAtUtc,
                run.StartedAtUtc,
                now,
                message,
                run.InputsJson,
                run.OptionsJson),
            ct).ConfigureAwait(false);

        return true;
    }

    private static bool IsTerminal(JobRunState status)
        => status is JobRunState.Succeeded or JobRunState.Failed or JobRunState.Cancelled or JobRunState.TimedOut;

    private static bool IsTerminal(JobStepRunState status)
        => status is JobStepRunState.Succeeded or JobStepRunState.Failed or JobStepRunState.Skipped;

    private static bool IsTerminalTaskStatus(string? status)
        => string.Equals(status, TaskStatuses.Completed, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, TaskStatuses.Failed, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, TaskStatuses.Cancelled, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, TaskStatuses.TimedOut, StringComparison.OrdinalIgnoreCase);

}

public sealed class JobRunReconciliationHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobRunReconciliationHostedService> _logger;

    public JobRunReconciliationHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<JobRunReconciliationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var reconciler = scope.ServiceProvider.GetRequiredService<JobRunReconciliationService>();
            await reconciler.ReconcileAsync(DateTimeOffset.UtcNow, ct: stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Job run reconciliation failed.");
        }
    }
}

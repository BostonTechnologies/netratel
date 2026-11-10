using NetRatel.Shared.Contracts.Jobs;
using Hangfire.Server;

namespace NetRatel.API.Services.Jobs;

/// <summary>
/// Runs recurring jobs through the same PostgreSQL/Akka authority as the
/// interactive job-run endpoints. Hangfire is only the scheduler.
/// </summary>
[JobScheduleOccurrence]
public sealed class HangfireJobDispatcher(IAkkaJobAuthorityService authority)
{
    public Task RunScheduledJobAsync(ulong jobId, PerformContext? context, CancellationToken cancellationToken = default)
    {
        var occurrence = context?.BackgroundJob?.Id;
        if (string.IsNullOrWhiteSpace(occurrence))
            throw new InvalidOperationException("The scheduler did not provide its stable background-job occurrence identity.");
        return authority.StartScheduledAsync(jobId, occurrence, cancellationToken);
    }

    public Task RunScheduledJobAsync(ulong jobId, CancellationToken cancellationToken = default)
        => authority.StartScheduledAsync(jobId, JobScheduleOccurrenceContext.Current
            ?? throw new InvalidOperationException("The retained scheduled-job entry point requires its real Hangfire occurrence context."), cancellationToken);
}

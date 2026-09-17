using Hangfire;
using NetRatel.Application.Jobs;

namespace NetRatel.API.Services.Jobs;

public sealed class JobScheduleRecurringJobSyncService(
    IRecurringJobManager recurringJobs,
    ILogger<JobScheduleRecurringJobSyncService> logger)
{
    public static string BuildRecurringJobId(ulong jobId) => $"netratel-job:{jobId}";

    public void Sync(JobDefinitionInfo job)
    {
        var recurringJobId = BuildRecurringJobId(job.Id);
        var schedule = JobSchedulePolicy.FromJob(job);
        if (schedule is null)
        {
            recurringJobs.RemoveIfExists(recurringJobId);
            logger.LogInformation("Removed recurring schedule for Job {JobId}.", job.Id);
            return;
        }

        recurringJobs.AddOrUpdate<HangfireJobDispatcher>(
            recurringJobId,
            dispatcher => dispatcher.RunScheduledJobAsync(job.Id, CancellationToken.None),
            schedule.ToCronExpression(),
            new RecurringJobOptions
            {
                TimeZone = schedule.ResolveTimeZone()
            });

        logger.LogInformation(
            "Synced recurring schedule for Job {JobId}: {Cron} {TimeZoneId}.",
            job.Id,
            schedule.ToCronExpression(),
            schedule.TimeZoneId);
    }

    public void Remove(ulong jobId)
    {
        recurringJobs.RemoveIfExists(BuildRecurringJobId(jobId));
        logger.LogInformation("Removed recurring schedule for deleted Job {JobId}.", jobId);
    }
}

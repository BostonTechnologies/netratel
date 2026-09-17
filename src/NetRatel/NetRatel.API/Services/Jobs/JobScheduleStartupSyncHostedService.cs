using NetRatel.Application.Jobs;

namespace NetRatel.API.Services.Jobs;

public sealed class JobScheduleStartupSyncHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<JobScheduleStartupSyncHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IJobDefinitionService>();
            var sync = scope.ServiceProvider.GetRequiredService<JobScheduleRecurringJobSyncService>();

            foreach (var job in await jobs.ListAsync(cancellationToken))
            {
                sync.Sync(job);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync recurring job schedules during API startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

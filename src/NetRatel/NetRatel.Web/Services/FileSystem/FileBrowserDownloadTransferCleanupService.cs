namespace NetRatel.Web.Services.FileSystem;

/// <summary>Runs one bounded periodic cleanup loop for retained transfer metadata.</summary>
public sealed class FileBrowserDownloadTransferCleanupService(
    IFileBrowserDownloadTransferRegistry downloads,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            downloads.CleanupExpired();
        }
    }
}

using NetRatel.Application.Operations;

namespace NetRatel.API.Services;

/// <summary>
/// Wipes expired V2 operator file-artifact bytes even when no caller returns
/// to clean them explicitly. The durable metadata remains as bounded evidence
/// that the artifact expired or was intentionally cleaned.
/// </summary>
public sealed class McpOperatorFileArtifactRetentionService(
    IServiceScopeFactory scopes,
    ILogger<McpOperatorFileArtifactRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IMcpOperatorFileArtifactStore>()
                    .PurgeExpiredAsync(DateTimeOffset.UtcNow, 100, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not purge expired V2 MCP file artifacts; the next sweep will retry.");
            }
        }
    }
}

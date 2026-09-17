using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services;

/// <summary>
/// Removes retained bytes from expired Development MCP file artifacts without
/// relying on a later operator request to trigger cleanup.
/// </summary>
public sealed class DevelopmentMcpFileArtifactRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<DevelopmentMcpFileArtifactRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = DateTimeOffset.UtcNow;
        var expired = await db.DevelopmentMcpFileArtifacts
            .Where(artifact => artifact.ExpiresAtUtc <= now && artifact.DeletedAtUtc == null)
            .OrderBy(artifact => artifact.ExpiresAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var artifact in expired)
        {
            artifact.Content = [];
            artifact.DeletedAtUtc = now;
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return expired.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not purge expired Development MCP file artifacts; the next sweep will retry.");
        }
    }
}

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Endpoints.Search;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services.Search;

/// <summary>Compiles and opens the bounded Global Search query paths before the first user search.</summary>
public sealed class GlobalSearchQueryWarmupService(
    IServiceScopeFactory scopeFactory,
    ILogger<GlobalSearchQueryWarmupService> logger) : IHostedService
{
    private const string WarmupTerm = "netratel-search-warmup";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await GlobalSearchEndpoints.BuildAgentQuery(db, WarmupTerm).Take(1).LoadAsync(cancellationToken).ConfigureAwait(false);
            await GlobalSearchEndpoints.BuildJobQuery(db, WarmupTerm).Take(1).LoadAsync(cancellationToken).ConfigureAwait(false);
            await GlobalSearchEndpoints.BuildRequestQuery(db, WarmupTerm).Take(1).LoadAsync(cancellationToken).ConfigureAwait(false);
            await GlobalSearchEndpoints.BuildTaskQuery(db, WarmupTerm).Take(1).LoadAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Global Search query warmup completed. QueryCount={QueryCount} ElapsedMs={ElapsedMs:F3}",
                4,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Global Search query warmup cancelled during application startup.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Global Search query warmup failed after {ElapsedMs:F3} ms; endpoint requests will compile queries on demand.",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Monitoring;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services.Monitoring;

/// <summary>Server-owned presence scans run even when every client has stopped sending telemetry.</summary>
public sealed class MonitoringOfflineWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<MonitoringOfflineWorker> logger) : BackgroundService
{
    private const int TenantsPerBatch = 8;
    private const int MaximumBatchesPerPass = 32;
    private int? _afterTenant;

    public async Task RunBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var tenants = await ReadPageAsync(_afterTenant, cancellationToken).ConfigureAwait(false);
        if (tenants.Length == 0 && _afterTenant is not null) tenants = await ReadPageAsync(null, cancellationToken).ConfigureAwait(false);
        if (tenants.Length == 0) { _afterTenant = null; return; }
        var store = scope.ServiceProvider.GetRequiredService<IMonitoringStore>();
        foreach (var tenant in tenants.Take(TenantsPerBatch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _afterTenant = tenant;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
            using var token = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try { await store.EvaluateOfflineAsync(tenant, token.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { logger.LogWarning("Offline monitoring scan exceeded its time budget for tenant {TenantId}.", tenant); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            { logger.LogWarning(exception, "Offline monitoring scan will resume for tenant {TenantId}.", tenant); }
        }
        if (tenants.Length <= TenantsPerBatch) _afterTenant = null;

        Task<int[]> ReadPageAsync(int? after, CancellationToken ct) => db.MonitoringRules.AsNoTracking()
            .Where(row => after == null || row.TenantId > after).Select(row => row.TenantId).Distinct().Order()
            .Take(TenantsPerBatch + 1).ToArrayAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), clock);
                using var token = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
                try
                {
                    // Continue through tenant pages promptly; waiting five seconds per page would age otherwise fresh evidence.
                    for (var batch = 0; batch < MaximumBatchesPerPass; batch++)
                    {
                        await RunBatchAsync(token.Token).ConfigureAwait(false);
                        if (_afterTenant is null) break;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                { logger.LogWarning("Offline monitoring reached its pass time budget; the next interval resumes after tenant {TenantId}.", _afterTenant); }
                catch (Exception exception) { logger.LogWarning(exception, "Offline monitoring scan will retry on the next interval."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
    }
}

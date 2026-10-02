using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Monitoring;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services.Monitoring;

/// <summary>Bounded tenant keyset rotation; all storage/runtime operations own their contexts or scopes.</summary>
public sealed class MonitoringFlowOutboxWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<MonitoringFlowOutboxWorker> logger) : BackgroundService
{
    private const int TenantsPerBatch = 8;
    private const int ItemsPerTenant = 4;
    private readonly Guid _workerId = Guid.NewGuid();
    private readonly SemaphoreSlim _batchGate = new(1, 1);
    private int? _afterTenant;

    public async Task RunBatchAsync(CancellationToken cancellationToken)
    {
        await _batchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), clock);
            using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var tenants = await ReadTenantPageAsync(_afterTenant, batch.Token).ConfigureAwait(false);
            if (tenants.Length == 0 && _afterTenant is not null) tenants = await ReadTenantPageAsync(null, batch.Token).ConfigureAwait(false);
            if (tenants.Length == 0) { _afterTenant = null; return; }
            foreach (var tenant in tenants.Take(TenantsPerBatch))
            {
                batch.Token.ThrowIfCancellationRequested();
                _afterTenant = tenant;
                using var tenantDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
                using var tenantToken = CancellationTokenSource.CreateLinkedTokenSource(batch.Token, tenantDeadline.Token);
                try { await RunTenantAsync(tenant, tenantToken.Token).WaitAsync(tenantToken.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!batch.IsCancellationRequested) { }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { logger.LogWarning("Monitoring flow delivery is pending for a tenant."); }
            }
            if (tenants.Length <= TenantsPerBatch) _afterTenant = null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        finally { _batchGate.Release(); }
    }

    private async Task<int[]> ReadTenantPageAsync(int? after, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = clock.GetUtcNow();
        return await db.MonitoringFlowOutbox.AsNoTracking().Where(row =>
            (after == null || row.TenantId > after) &&
            (row.Status == MonitoringOutboxStatus.Pending || row.Status == MonitoringOutboxStatus.Leased && row.LeaseExpiresAtUtc <= now ||
                row.Status == MonitoringOutboxStatus.DeliveryUnknown && (row.FlowRunId != null || row.Attempts > 0)) &&
            (row.NextAttemptAtUtc == null || row.NextAttemptAtUtc <= now))
            .Select(row => row.TenantId).Distinct().Order().Take(TenantsPerBatch + 1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMonitoringStore>();
        var processor = scope.ServiceProvider.GetRequiredService<MonitoringFlowOutboxProcessor>();
        var receipts = await store.ListUnsettledFlowRunsAsync(tenantId, ItemsPerTenant, cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(receipts, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            (receipt, token) => AttemptAsync(attempt => processor.ReconcileReceiptAsync(receipt, attempt), token)).ConfigureAwait(false);
        var leases = await store.ClaimOutboxAsync(new(tenantId, _workerId, ItemsPerTenant, TimeSpan.FromSeconds(60)), cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(leases, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            (lease, token) => AttemptAsync(attempt => processor.ProcessLeaseAsync(lease, attempt), token)).ConfigureAwait(false);
    }

    private async ValueTask AttemptAsync(Func<CancellationToken, Task<bool>> action, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2), clock);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            await action(attempt.Token).WaitAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { logger.LogWarning("Monitoring flow receipt remains unsettled; persisted ownership controls recovery."); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await RunBatchAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception) { logger.LogWarning("Monitoring flow delivery batch is pending."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

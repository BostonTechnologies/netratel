using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NetRatel.Infrastructure.Metrics;

public sealed class MetricHistoryMaintenance(IServiceScopeFactory scopes, TimeProvider time, ILogger<MetricHistoryMaintenance> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var after = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12), time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
                await using var discovery = scopes.CreateAsyncScope();
                var tenants = await discovery.ServiceProvider.GetRequiredService<MetricHistoryStore>()
                    .MaintenanceTenantsAsync(after, linked.Token).ConfigureAwait(false);
                foreach (var tenant in tenants)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    after = tenant;
                    await MaintainTenantAsync(tenant, linked.Token).ConfigureAwait(false);
                }
                if (tenants.Count < 32) after = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Historical metrics maintenance pass is incomplete.");
            }
        }
    }

    private async Task MaintainTenantAsync(int tenant, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MetricHistoryStore>().MaintainAsync(tenant, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Historical metrics maintenance is pending for tenant {TenantId}.", tenant);
        }
    }
}

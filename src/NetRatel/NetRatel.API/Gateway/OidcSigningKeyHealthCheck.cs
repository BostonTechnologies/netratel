using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Infrastructure.Services;

namespace NetRatel.API.Gateway;

public sealed class OidcSigningKeyHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var signingService = scope.ServiceProvider.GetRequiredService<OidcSigningService>();
            await signingService.GetActiveSigningKeyAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Agent token signing is available.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "Agent token signing is unavailable.",
                data: new Dictionary<string, object>
                {
                    ["failureType"] = exception.GetType().Name
                });
        }
    }
}

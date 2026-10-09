using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.Infrastructure.SystemPairing.Network;
namespace NetRatel.Infrastructure.SystemPairing;
public static class PairingRegistration
{
    public static IServiceCollection AddSystemPairing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<InstallationIdentityStore>(); services.AddScoped<PairingAuthority>(); services.AddScoped<PairingService>(); services.AddScoped<PairingBusinessProfileService>();
        services.AddHttpClient<PairingTransport>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) => { for (var i = handlers.Count - 1; i >= 0; i--) if (handlers[i].GetType().FullName == "Microsoft.Extensions.Http.Resilience.ResilienceHandler") handlers.RemoveAt(i); })
            .ConfigurePrimaryHttpMessageHandler(() => PairingSafeHttpMessageHandler.Create(true));
        services.AddHostedService<PairingCleanupWorker>(); return services;
    }
}
public sealed class PairingCleanupWorker(IServiceScopeFactory scopes, ILogger<PairingCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await using var scope = scopes.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<PairingService>().CleanupAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Pairing cleanup unavailable: {FailureType}", error.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}

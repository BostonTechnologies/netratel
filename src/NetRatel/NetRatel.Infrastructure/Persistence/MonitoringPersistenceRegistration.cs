using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.Application.Monitoring;

namespace NetRatel.Infrastructure.Persistence;

public static class MonitoringPersistenceRegistration
{
    public static IServiceCollection AddNetRatelMonitoringPersistence(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMonitoringPublishedFlowProvider, UnavailableMonitoringPublishedFlowProvider>();
        services.TryAddSingleton<MonitoringStore>();
        services.TryAddSingleton<IMonitoringStore>(provider => provider.GetRequiredService<MonitoringStore>());
        services.TryAddSingleton<IMonitoringConfigurationStore>(provider => provider.GetRequiredService<MonitoringStore>());
        services.TryAddSingleton<IMonitoringAgentEligibility, MonitoringAgentEligibility>();
        return services;
    }
}

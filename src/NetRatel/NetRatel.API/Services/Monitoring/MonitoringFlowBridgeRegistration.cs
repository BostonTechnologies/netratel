using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.Application.Flows;
using NetRatel.Application.Monitoring;

namespace NetRatel.API.Services.Monitoring;

public static class MonitoringFlowBridgeRegistration
{
    /// <summary>Call after monitoring persistence and real Flows registration.</summary>
    public static IServiceCollection AddMonitoringFlowBridge(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IMonitoringPublishedFlowProvider, PublishedMonitoringFlowProvider>());
        services.Replace(ServiceDescriptor.Scoped<IFlowDispatchGuard, MonitoringFlowDispatchGuard>());
        services.TryAddSingleton<MonitoringFlowOutboxProcessor>();
        services.AddHostedService<MonitoringFlowOutboxWorker>();
        return services;
    }
}

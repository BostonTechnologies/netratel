using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Persistence;
namespace NetRatel.Infrastructure.RatelDesk;
public static class RatelDeskConnectorRegistration
{
    public static IServiceCollection AddRatelDeskConnector(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System); services.TryAddSingleton<RatelDeskTransportLimiter>();
        services.TryAddScoped<IRatelDeskConnectorStore, RatelDeskConnectorStore>(); services.TryAddScoped<IRatelDeskConnectorAuthorization, RatelDeskConnectorAuthorization>();
        services.TryAddScoped<IRatelDeskConnectorService, RatelDeskConnectorService>(); services.TryAddScoped<RatelDeskFlowConnector>();
        services.AddScoped<IFlowConnectorCatalog>(p => p.GetRequiredService<RatelDeskFlowConnector>());
        services.AddScoped<IFlowIncidentActionDispatcher>(p => p.GetRequiredService<RatelDeskFlowConnector>()); return services;
    }
}

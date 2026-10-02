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
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRatelDeskOriginPolicy, RatelDeskOriginPolicy>();
        services.TryAddSingleton<RatelDeskTransportLimiter>();
        services.AddHttpClient<RatelDeskHttpTransport>(http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        services.TryAddScoped<IRatelDeskConnectionTester>(provider => provider.GetRequiredService<RatelDeskHttpTransport>());
        services.TryAddScoped<IRatelDeskIncidentTransport>(provider => provider.GetRequiredService<RatelDeskHttpTransport>());
        services.TryAddScoped<IRatelDeskConnectorStore, RatelDeskConnectorStore>();
        services.TryAddScoped<IRatelDeskConnectorAuthorization, RatelDeskConnectorAuthorization>();
        services.TryAddScoped<IRatelDeskConnectorService, RatelDeskConnectorService>();
        services.TryAddScoped<RatelDeskFlowConnector>();
        services.AddScoped<IFlowConnectorCatalog>(provider => provider.GetRequiredService<RatelDeskFlowConnector>());
        services.AddScoped<IFlowIncidentActionDispatcher>(provider => provider.GetRequiredService<RatelDeskFlowConnector>());
        return services;
    }
}

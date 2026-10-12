using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.API.Services.Notifications;
using NetRatel.Application.Flows;
using NetRatel.Application.Notifications;
using NetRatel.Infrastructure.Notifications;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Bootstrap;

public static class NotificationConnectorRegistration
{
    public static IServiceCollection AddNotificationConnectors(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<INotificationCredentialProtector, NotificationCredentialProtector>();
        services.TryAddSingleton<INotificationWebhookTransport, NotificationWebhookTransport>();
        services.TryAddScoped<INotificationConnectorStore, NotificationConnectorStore>();
        services.TryAddScoped<INotificationConnectorService, NotificationConnectorService>();
        services.TryAddScoped<NotificationFlowConnector>();
        services.TryAddScoped<IFlowNotificationActionDispatcher>(p => p.GetRequiredService<NotificationFlowConnector>());
        services.Replace(ServiceDescriptor.Scoped<IFlowConnectorCatalog, NotificationAndIncidentConnectorCatalog>());
        return services;
    }
}

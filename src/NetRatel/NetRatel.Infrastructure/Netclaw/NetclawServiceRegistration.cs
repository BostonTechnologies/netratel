using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.Application.Flows;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Notifications;

namespace NetRatel.Infrastructure.Netclaw;

public static class NetclawServiceRegistration
{
    public static IServiceCollection AddNetclawInvestigations(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<INetclawPairingService, NetclawPairingService>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => IntegrationSafeHttpMessageHandler.Create())
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
            {
                // Pairing codes are single use; an uncertain POST must never be replayed by global HTTP defaults.
                for (var index = handlers.Count - 1; index >= 0; index--)
                    if (handlers[index].GetType().FullName == "Microsoft.Extensions.Http.Resilience.ResilienceHandler") handlers.RemoveAt(index);
            });
        services.TryAddSingleton<IAiAssistantChatClientFactory, AiAssistantChatClientFactory>();
        services.TryAddScoped<NetclawProfileService>(); services.TryAddScoped<NetclawAuthority>();
        services.TryAddScoped<PromptLibraryService>();
        services.TryAddScoped<IPromptLibraryService>(provider => provider.GetRequiredService<PromptLibraryService>());
        services.TryAddScoped<InvestigationStore>(); services.TryAddScoped<NetclawFlowConnector>();
        services.TryAddScoped<NotificationAndIncidentConnectorCatalog>();
        services.Replace(ServiceDescriptor.Scoped<IFlowConnectorCatalog, NetclawConnectorCatalog>());
        services.TryAddSingleton<NetclawInvestigationWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<NetclawInvestigationWorker>());
        return services;
    }
}

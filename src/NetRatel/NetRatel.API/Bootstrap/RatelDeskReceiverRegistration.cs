using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.RatelDesk;

namespace NetRatel.API.Bootstrap;

public static class RatelDeskReceiverRegistration
{
    public static IServiceCollection AddRatelDeskReceiverAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RatelDeskReceiverOptions>().Bind(configuration.GetSection(RatelDeskReceiverOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RatelDeskReceiverOptions>, RatelDeskReceiverOptionsValidator>());
        services.TryAddSingleton<RatelDeskReceiverNetworkPolicy>();
        foreach (var (name, mode) in new[]
        {
            (RatelDeskReceiverHttpPipeline.ManualClient, RatelDeskAuthenticationMode.ManualApiBearer),
            (RatelDeskReceiverHttpPipeline.ManagedClient, RatelDeskAuthenticationMode.ManagedServiceLink)
        })
        {
            services.AddHttpClient(name, http => http.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
                {
                    // Receiver attempts and deadlines are durable application policy. A global
                    // resilience handler must never replay a POST or introduce another budget.
                    for (var index = handlers.Count - 1; index >= 0; index--)
                        if (handlers[index] is Microsoft.Extensions.Http.Resilience.ResilienceHandler)
                            handlers.RemoveAt(index);
                })
                .ConfigurePrimaryHttpMessageHandler(provider => RatelDeskReceiverSafeHttpMessageHandler.Create(
                    mode, provider.GetRequiredService<RatelDeskReceiverNetworkPolicy>()));
        }
        services.TryAddSingleton<IRatelDeskReceiverFingerprint, RatelDeskReceiverFingerprint>();
        services.TryAddSingleton<ReceiverPreparationBuilder>();
        services.TryAddSingleton<IFlowSourceIdentityResolver>(p => p.GetRequiredService<FlowPersistenceService>());
        services.TryAddSingleton<IFlowReceiverEvidenceStore>(p => p.GetRequiredService<FlowPersistenceService>());
        services.TryAddScoped<RatelDeskConnectorStore>();
        services.Replace(ServiceDescriptor.Scoped<IRatelDeskConnectorStore>(p => p.GetRequiredService<RatelDeskConnectorStore>()));
        services.TryAddScoped<IRatelDeskConnectorBindingStore>(p => p.GetRequiredService<RatelDeskConnectorStore>());
        services.TryAddScoped<IRatelDeskConnectorReadinessStore>(p => p.GetRequiredService<RatelDeskConnectorStore>());
        services.TryAddScoped<RatelDeskReceiverHttpPipeline>();
        services.TryAddScoped<IRatelDeskManualProfileProbe, RatelDeskManualProfileProbe>();
        services.TryAddScoped<IRatelDeskProducerContinuity, RatelDeskProducerContinuity>();
        services.TryAddScoped<IRatelDeskInstallationIdentityReader, RatelDeskInstallationIdentityReader>();
        services.TryAddScoped<ManualRatelDeskBindingResolver>();
        services.TryAddScoped<ManagedRatelDeskBindingResolver>();
        services.TryAddScoped<IRatelDeskOutboundBindingResolver, RatelDeskOutboundBindingResolver>();
        services.TryAddScoped<IRatelDeskReceiverTransport, RatelDeskReceiverTransport>();
        services.TryAddScoped<IRatelDeskConnectorReadiness, RatelDeskConnectorReadiness>();
        services.TryAddScoped<RatelDeskConnectorReceiver>();
        services.TryAddScoped<IRatelDeskConnectorSetupService, RatelDeskConnectorSetupService>();
        services.TryAddScoped<IFlowReceiverDispatcher, RatelDeskFlowReceiverAdapter>();
        return services;
    }
}

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
        services.AddHttpClient(RatelDeskReceiverHttpPipeline.ManagedClient, http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
            {
                // Approved external receiver endpoints and durable receipt keys must
                // reach the strict socket policy with their original authority/path bytes.
                for (var i = handlers.Count - 1; i >= 0; i--)
                    if (handlers[i] is Microsoft.Extensions.Http.Resilience.ResilienceHandler ||
                        handlers[i].GetType().FullName == "Microsoft.Extensions.ServiceDiscovery.Http.ResolvingHttpDelegatingHandler") handlers.RemoveAt(i);
            })
            .ConfigurePrimaryHttpMessageHandler(p => RatelDeskReceiverSafeHttpMessageHandler.Create(RatelDeskAuthenticationMode.PairedSystem, p.GetRequiredService<RatelDeskReceiverNetworkPolicy>()));
        services.TryAddSingleton<IRatelDeskReceiverFingerprint, RatelDeskReceiverFingerprint>();
        services.TryAddSingleton<ReceiverPreparationBuilder>();
        services.TryAddSingleton<IFlowSourceIdentityResolver>(p => p.GetRequiredService<FlowPersistenceService>());
        services.TryAddSingleton<IFlowReceiverEvidenceStore>(p => p.GetRequiredService<FlowPersistenceService>());
        services.TryAddScoped<RatelDeskConnectorStore>();
        services.Replace(ServiceDescriptor.Scoped<IRatelDeskConnectorStore>(p => p.GetRequiredService<RatelDeskConnectorStore>()));
        services.TryAddScoped<IRatelDeskConnectorBindingStore>(p => p.GetRequiredService<RatelDeskConnectorStore>());
        services.TryAddScoped<IRatelDeskConnectorReadinessStore>(p => p.GetRequiredService<RatelDeskConnectorStore>());
        services.TryAddScoped<RatelDeskReceiverHttpPipeline>();
        services.TryAddScoped<IRatelDeskProducerContinuity, RatelDeskProducerContinuity>();
        services.TryAddScoped<IRatelDeskInstallationIdentityReader, RatelDeskInstallationIdentityReader>();
        services.TryAddScoped<IRatelDeskOutboundBindingResolver, PairingRatelDeskBindingResolver>();
        services.TryAddScoped<IRatelDeskReceiverTransport, RatelDeskReceiverTransport>();
        services.TryAddScoped<IRatelDeskConnectorReadiness, RatelDeskConnectorReadiness>();
        services.TryAddScoped<RatelDeskConnectorReceiver>();
        services.TryAddScoped<IFlowReceiverDispatcher, RatelDeskFlowReceiverAdapter>();
        return services;
    }
}

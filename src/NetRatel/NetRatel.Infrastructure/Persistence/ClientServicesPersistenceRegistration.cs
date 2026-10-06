using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.Application.Services;
using NetRatel.Application.Presence;

namespace NetRatel.Infrastructure.Persistence;

public static class ClientServicesPersistenceRegistration
{
    public static IServiceCollection AddNetRatelClientServicesPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClientServicesStore, ClientServicesStore>();
        // OwnershipPolicy is supplied from the API's validated Akka options.
        // The singleton store owns no DbContext: each operation creates a scope.
        services.TryAddSingleton<IClientConnectionEpochStore, ClientConnectionEpochStore>();
        return services;
    }
}

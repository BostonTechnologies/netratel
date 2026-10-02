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
        services.TryAddSingleton<IClientConnectionEpochStore, ClientConnectionEpochStore>();
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.Application.Jobs;

namespace NetRatel.Infrastructure.Persistence;

public static class JobObservationPersistenceRegistration
{
    public static IServiceCollection AddNetRatelJobObservationPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJobObservationStore, JobObservationStore>();
        return services;
    }
}

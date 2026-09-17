using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.Application.Jobs;

namespace NetRatel.Infrastructure.Persistence;

public static class JobShadowPersistenceRegistration
{
    public static IServiceCollection AddNetRatelJobShadowPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJobShadowPersistenceStore, JobShadowPersistenceStore>();
        return services;
    }
}

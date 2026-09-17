using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.Application.RemoteSupport;

namespace NetRatel.Infrastructure.Persistence;

public static class RemoteSupportLifecycleRegistration
{
    public static IServiceCollection AddNetRatelRemoteSupportLifecycle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRemoteSupportLifecycleStore, RemoteSupportLifecycleStore>();
        return services;
    }
}

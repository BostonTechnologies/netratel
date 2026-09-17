using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.Application.Commands;

namespace NetRatel.Infrastructure.Persistence;

public static class CommandPersistenceRegistration
{
    public static IServiceCollection AddNetRatelCommandPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<CommandInbox>();
        services.TryAddScoped<CommandOutbox>();
        services.TryAddScoped<CommandIntentHistory>();
        services.TryAddSingleton<ICommandPersistenceStore, CommandPersistenceStore>();
        return services;
    }
}

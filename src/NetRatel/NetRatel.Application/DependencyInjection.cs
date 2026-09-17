using Microsoft.Extensions.DependencyInjection;

namespace NetRatel.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddNetRatelApplication(this IServiceCollection services)
    {
        return services;
    }
}

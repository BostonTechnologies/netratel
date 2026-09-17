using Microsoft.Extensions.DependencyInjection;

namespace NetRatel.Mcp.Core;

public static class NetRatelMcpCoreServiceCollectionExtensions
{
    /// <summary>Registers one immutable host context for a single MCP process.</summary>
    public static IServiceCollection AddNetRatelMcpCore(this IServiceCollection services, NetRatelMcpHostContext hostContext)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hostContext);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(NetRatelMcpHostContext)))
        {
            throw new InvalidOperationException("Only one NetRatel MCP host context may be registered in a service provider.");
        }

        services.AddSingleton(hostContext);
        services.AddSingleton(hostContext.Target);
        services.AddTransient<NetRatelMcpCatalogTools>();
        services.AddTransient<NetRatelMcpOperationalTools>();
        services.AddTransient<NetRatelMcpStdioNotificationTools>();
        return services;
    }
}

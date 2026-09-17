using NetRatel.Akka.Configuration;

namespace NetRatel.API.Realtime.Shadow;

public static class SignalRShadowEndpointRegistrationExtensions
{
    public const string HubPath = "/hubs/akka-shadow";

    public static IEndpointRouteBuilder MapSignalRShadowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<NetRatelAkkaMigrationOptions>();
        var environment = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>();
        if (!options.Enabled || !options.SignalRShadowEnabled || options.IsSignalRAuthorityActive ||
            !options.SignalRShadowLocalCanaryEnabled || !environment.IsDevelopment() ||
            endpoints.ServiceProvider.GetService<IShadowFanoutSink>() is not SignalRShadowFanoutBridge)
        {
            return endpoints;
        }

        endpoints.MapHub<AkkaShadowHub>(HubPath, hubOptions =>
            {
                hubOptions.ApplicationMaxBufferSize = 16 * 1024;
                hubOptions.TransportMaxBufferSize = 64 * 1024;
            })
            .RequireAuthorization("AkkaShadowAccess");

        return endpoints;
    }

    public const string AuthorityHubPath = "/hubs/akka-authority";

    public static IEndpointRouteBuilder MapSignalRAuthorityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<NetRatelAkkaMigrationOptions>();
        var environment = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>();
        if (!options.IsSignalRAuthorityActive || !environment.IsDevelopment() ||
            endpoints.ServiceProvider.GetService<IShadowFanoutSink>() is not SignalRShadowFanoutBridge)
        {
            return endpoints;
        }

        endpoints.MapHub<AkkaShadowHub>(AuthorityHubPath, hubOptions =>
            {
                hubOptions.ApplicationMaxBufferSize = 16 * 1024;
                hubOptions.TransportMaxBufferSize = 64 * 1024;
            })
            .RequireAuthorization("AkkaShadowAccess");
        return endpoints;
    }
}

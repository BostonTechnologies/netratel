using NetRatel.Akka.Configuration;

namespace NetRatel.API.Realtime.Operations;

public static class OperationsLogEndpointRegistrationExtensions
{
    public static IEndpointRouteBuilder MapOperationsLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.ServiceProvider.GetRequiredService<NetRatelAkkaMigrationOptions>().IsLogAuthorityActive)
        {
            endpoints.MapHub<OperationsHub>(OperationsHub.HubPath, options =>
                {
                    options.ApplicationMaxBufferSize = 16 * 1024;
                    options.TransportMaxBufferSize = 64 * 1024;
                })
                .RequireAuthorization("Operator");
        }

        return endpoints;
    }
}

namespace NetRatel.API.Realtime.Operations;

public static class OperationsLogEndpointRegistrationExtensions
{
    public static IEndpointRouteBuilder MapOperationsLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHub<OperationsHub>(OperationsHub.HubPath, options =>
            {
                options.ApplicationMaxBufferSize = 16 * 1024;
                options.TransportMaxBufferSize = 64 * 1024;
            })
            .RequireAuthorization("OperationsLogAccess");

        return endpoints;
    }
}

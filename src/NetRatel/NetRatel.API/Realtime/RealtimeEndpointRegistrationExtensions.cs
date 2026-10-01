namespace NetRatel.API.Realtime;

public static class RealtimeEndpointRegistrationExtensions
{
    public const string AuthorityHubPath = "/hubs/akka-authority";

    public static IEndpointRouteBuilder MapSignalRAuthorityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHub<RealtimeHub>(AuthorityHubPath, hubOptions =>
            {
                hubOptions.ApplicationMaxBufferSize = 16 * 1024;
                hubOptions.TransportMaxBufferSize = 64 * 1024;
            })
            .RequireAuthorization("RealtimeAccess");
        return endpoints;
    }
}

using NetRatel.Web.Configuration;
using Microsoft.Extensions.Http.Resilience;
using NetRatel.Web.Services.Authentication;

namespace NetRatel.Web.Services.Monitoring;

public static class MonitoringHttpClientRegistration
{
    public static IServiceCollection AddMonitoringApiClient(this IServiceCollection services, IConfiguration configuration) =>
        services.AddMonitoringApiClient(WebGatewayOptions.ResolveApiBaseUri(configuration));

    public static IServiceCollection AddMonitoringApiClient(this IServiceCollection services, Uri apiBaseUri)
    {
        services.AddHttpClient(MonitoringApiService.ClientName, client =>
        {
            client.BaseAddress = apiBaseUri;
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        .AddHttpMessageHandler<TokenAuthorizationHandler>()
        .RemoveAllResilienceHandlers();
        return services;
    }
}

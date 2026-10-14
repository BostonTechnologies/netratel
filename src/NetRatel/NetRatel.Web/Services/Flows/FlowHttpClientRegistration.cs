using NetRatel.Web.Configuration;
using Microsoft.Extensions.Http.Resilience;
using NetRatel.Web.Services.Authentication;

namespace NetRatel.Web.Services.Flows;

public static class FlowHttpClientRegistration
{
    public static IServiceCollection AddFlowsApiClient(this IServiceCollection services, IConfiguration configuration) =>
        services.AddFlowsApiClient(WebGatewayOptions.ResolveApiBaseUri(configuration));

    public static IServiceCollection AddFlowsApiClient(this IServiceCollection services, Uri apiBaseUri)
    {
        services.AddHttpClient(FlowApiService.ClientName, client =>
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

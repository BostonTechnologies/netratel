using Microsoft.Extensions.Http.Resilience;
using NetRatel.Web.Services.Authentication;

namespace NetRatel.Web.Services.Flows;

public static class FlowHttpClientRegistration
{
    public static IServiceCollection AddFlowsApiClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(FlowApiService.ClientName, client =>
        {
            client.BaseAddress = new Uri(configuration["ApiBaseUrl"] ?? "https://localhost:5001/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        .AddHttpMessageHandler<TokenAuthorizationHandler>()
        .RemoveAllResilienceHandlers();
        return services;
    }
}

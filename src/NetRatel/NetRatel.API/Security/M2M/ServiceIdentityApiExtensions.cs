using Microsoft.AspNetCore.Authentication;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.ServiceIdentity;

namespace NetRatel.API.Security.M2M;

public static class ServiceIdentityServiceCollectionExtensions
{
    public const string VerifyPolicy = "ServiceLinkVerify";
    public const string ControlPolicy = "ServiceLinkControl";
    public const string SensitiveRateLimiter = "ServiceIssuerSensitive";

    public static IServiceCollection AddNetRatelServiceIdentityApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddNetRatelServiceIdentity(configuration);
        services.AddOptions<M2MOptions>().ValidateOnStart();
        services.AddOptions<M2MDeploymentClientsOptions>().Bind(configuration.GetSection("M2MClients")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<M2MOptions>, M2MDeploymentOptionsValidator>();
        services.AddSingleton<IValidateOptions<M2MDeploymentClientsOptions>, M2MDeploymentClientsValidator>();
        services.AddSingleton<IM2MDeploymentProfileResolver>(sp => new M2MDeploymentProfileResolver(
            sp.GetRequiredService<IOptionsMonitor<M2MOptions>>(),
            sp.GetRequiredService<IOptionsMonitor<M2MDeploymentClientsOptions>>()));
        services.AddSingleton<IServiceClientDeploymentCatalog>(sp => sp.GetRequiredService<IM2MDeploymentProfileResolver>());
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ServiceIdentityAuthenticationHandler>(ServiceIdentityAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(o =>
        {
            foreach (var (policy, scope) in new[] { (VerifyPolicy, ServiceIdentityScopes.Verify), (ControlPolicy, ServiceIdentityScopes.Control) })
                o.AddPolicy(policy, p => p.AddAuthenticationSchemes(ServiceIdentityAuthenticationHandler.SchemeName).RequireAuthenticatedUser()
                    .RequireAssertion(c => c.User.FindAll("scope").SelectMany(x => x.Value.Split(' ')).Contains(scope, StringComparer.Ordinal)));
        });
        services.AddRateLimiter(o => o.AddPolicy(SensitiveRateLimiter, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
            { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
        return services;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NetRatel.Infrastructure.ServiceIdentity;

public static class ServiceIdentityInfrastructureExtensions
{
    public static IServiceCollection AddNetRatelServiceIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServiceIdentityOptions>().Bind(configuration.GetSection(ServiceIdentityOptions.SectionName)).ValidateOnStart();
        services.PostConfigure<ServiceIdentityOptions>(value => value.AllowPrivateHttp = true);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ServiceIdentityOptions>, ServiceIdentityOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IServiceClientDeploymentCatalog, EmptyServiceClientDeploymentCatalog>();
        services.AddScoped<IServicePublicSettingsResolver, ServicePublicSettingsResolver>();
        services.AddScoped<IServiceIdentityRuntimeOptions, ServiceIdentityRuntimeOptions>();
        services.AddScoped<IServicePrincipalRegistry, ServicePrincipalRegistry>();
        services.AddScoped<ServiceSigningKeyStore>();
        services.AddScoped<ServiceAccessTokenService>();
        return services;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.ServiceLinks;

namespace NetRatel.Infrastructure.ServiceIdentity;

public static class ServiceIdentityInfrastructureExtensions
{
    public static IServiceCollection AddNetRatelServiceIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServiceIdentityOptions>().Bind(configuration.GetSection(ServiceIdentityOptions.SectionName)).ValidateOnStart();
        // This single deployment boundary normalizes the deliberate HTTP opt-in
        // before either raw options validator runs. Evaluate it on each options
        // creation so reload cannot retain a previously enabled flag.
        bool AllowPrivateHttp() => configuration.GetValue<bool>("ServiceIdentity:AllowPrivateHttp") ||
            configuration.GetValue<bool>("ServiceLinks:AllowPrivateHttp");
        services.PostConfigure<ServiceIdentityOptions>(value => value.AllowPrivateHttp = AllowPrivateHttp());
        services.PostConfigure<ServiceLinkOptions>(value => value.AllowPrivateHttp = AllowPrivateHttp());
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

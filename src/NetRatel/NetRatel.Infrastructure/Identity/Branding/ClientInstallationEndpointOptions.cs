using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.Shared.Client;

namespace NetRatel.Infrastructure.Identity.Branding;

/// <summary>Deployment overrides for public client installation addresses.</summary>
public sealed class ClientInstallationEndpointOptions
{
    public const string SectionName = "ClientArtifacts";
    public string? PublicBaseUrl { get; set; }
    public string? PublicGatewayBaseUrl { get; set; }
}

public sealed class ClientInstallationEndpointOptionsValidator : IValidateOptions<ClientInstallationEndpointOptions>
{
    public ValidateOptionsResult Validate(string? name, ClientInstallationEndpointOptions options)
    {
        var failures = new List<string>();
        foreach (var (key, value, gateway) in new[]
                 {
                     (nameof(options.PublicBaseUrl), options.PublicBaseUrl, false),
                     (nameof(options.PublicGatewayBaseUrl), options.PublicGatewayBaseUrl, true)
                 })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            try
            {
                ClientEndpointAddress.NormalizePublicOrigin(gateway
                    ? ClientEndpointAddress.NormalizeGatewayBase(value)
                    : ClientEndpointAddress.NormalizeApiBase(value));
            }
            catch (ArgumentException) { failures.Add($"ClientArtifacts:{key} must be a public HTTPS origin."); }
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public static class ClientInstallationOptionsRegistration
{
    public static IServiceCollection AddClientInstallationOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<ClientInstallationEndpointOptions>, ClientInstallationEndpointOptionsValidator>();
        services.AddOptions<ClientInstallationEndpointOptions>()
            .Bind(configuration.GetSection(ClientInstallationEndpointOptions.SectionName))
            .PostConfigure(options =>
            {
                options.PublicBaseUrl = Optional(options.PublicBaseUrl);
                options.PublicGatewayBaseUrl = Optional(options.PublicGatewayBaseUrl);
            })
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<DeploymentBrandingOptions>, DeploymentBrandingOptionsValidator>();
        services.AddOptions<DeploymentBrandingOptions>()
            .Bind(configuration.GetSection(DeploymentBrandingOptions.SectionName))
            .PostConfigure<IOptionsMonitor<ClientInstallationEndpointOptions>>((branding, endpoints) =>
            {
                branding.SiteUrl = Optional(branding.SiteUrl);
                branding.GatewayUrl = Optional(branding.GatewayUrl);
                var legacyGateway = endpoints.CurrentValue.PublicGatewayBaseUrl;
                if (legacyGateway is null) return;
                string? canonicalGateway;
                try { canonicalGateway = branding.GatewayUrl is null ? null : ClientEndpointAddress.NormalizePublicOrigin(branding.GatewayUrl); }
                catch (ArgumentException)
                {
                    throw new OptionsValidationException(Options.DefaultName, typeof(DeploymentBrandingOptions),
                        ["Branding:GatewayUrl must be a public HTTPS origin."]);
                }
                if (canonicalGateway is not null &&
                    !string.Equals(canonicalGateway, ClientEndpointAddress.NormalizePublicOrigin(legacyGateway), StringComparison.OrdinalIgnoreCase))
                    throw new OptionsValidationException(Options.DefaultName, typeof(DeploymentBrandingOptions),
                        ["Branding:GatewayUrl conflicts with ClientArtifacts:PublicGatewayBaseUrl."]);
                // The compatibility key is deployment-owned too: the UI must show
                // the same locked gateway that installer generation will consume.
                branding.GatewayUrl = ClientEndpointAddress.NormalizePublicOrigin(legacyGateway);
            })
            .ValidateOnStart();
        return services;
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

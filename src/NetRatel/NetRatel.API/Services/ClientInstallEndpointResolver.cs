using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Shared.Client;

namespace NetRatel.API.Services;

/// <summary>One read-only public-address resolution path for previews and generated installers.</summary>
public static class ClientInstallEndpointResolver
{
    public static ClientInstallEndpointSummary Resolve(
        ClientInstallationEndpointOptions options, EffectiveDeploymentBranding branding)
    {
        var web = ValidatePublicOrigin(branding.SiteUrl.Value, "Web");
        var configuredApi = options.PublicBaseUrl;
        var api = NormalizeAndValidate(string.IsNullOrWhiteSpace(configuredApi) ? web : configuredApi, gateway: false);
        var configuredGateway = options.PublicGatewayBaseUrl;
        if (string.IsNullOrWhiteSpace(configuredGateway) && string.IsNullOrWhiteSpace(branding.GatewayUrl?.Value))
            throw new InvalidOperationException("Set the public gateway URL on Branding or in deployment configuration before generating installers.");
        var gateway = NormalizeAndValidate(string.IsNullOrWhiteSpace(configuredGateway)
            ? branding.GatewayUrl?.Value ?? string.Empty : configuredGateway, gateway: true);

        return new(web, api, gateway,
            $"branding-site-url:{branding.SiteUrl.Source.ToString().ToLowerInvariant()}",
            string.IsNullOrWhiteSpace(configuredApi) ? "branding-site-url" : "client-artifacts-public-base-url",
            string.IsNullOrWhiteSpace(configuredGateway)
                ? $"branding-gateway-url:{branding.GatewayUrl!.Source.ToString().ToLowerInvariant()}"
                : "client-artifacts-public-gateway-base-url");
    }

    public static string GatewayOverride(ClientInstallEndpointSummary endpoints) =>
        endpoints.EffectiveGatewayBaseUrl ?? throw new InvalidOperationException("Set the public gateway endpoint before generating installers.");

    private static string NormalizeAndValidate(string value, bool gateway)
    {
        try
        {
            return ValidatePublicOrigin(gateway ? ClientEndpointAddress.NormalizeGatewayBase(value)
                : ClientEndpointAddress.NormalizeApiBase(value), gateway ? "gateway" : "API");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    private static string ValidatePublicOrigin(string? value, string name)
    {
        try { return ClientEndpointAddress.NormalizePublicOrigin(value); }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"Set the public {name} endpoint to a public HTTPS origin before generating installers.");
        }
    }
}

using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Shared.Client;

namespace NetRatel.API.Services;

/// <summary>One read-only public-address resolution path for previews and generated installers.</summary>
public static class ClientInstallEndpointResolver
{
    public static ClientInstallEndpointSummary Resolve(
        IConfiguration configuration, EffectiveDeploymentBranding branding)
    {
        var web = ValidatePublicOrigin(branding.SiteUrl.Value, "Web");
        var configuredApi = configuration["ClientArtifacts:PublicBaseUrl"];
        var api = NormalizeAndValidate(string.IsNullOrWhiteSpace(configuredApi) ? web : configuredApi, gateway: false);
        var configuredGateway = configuration["ClientArtifacts:PublicGatewayBaseUrl"];
        var gateway = string.IsNullOrWhiteSpace(configuredGateway)
            ? api
            : NormalizeAndValidate(configuredGateway, gateway: true);

        return new(web, api, gateway,
            $"branding-site-url:{branding.SiteUrl.Source.ToString().ToLowerInvariant()}",
            string.IsNullOrWhiteSpace(configuredApi) ? "branding-site-url" : "client-artifacts-public-base-url",
            string.IsNullOrWhiteSpace(configuredGateway) ? "shared-api-origin" : "client-artifacts-public-gateway-base-url");
    }

    public static string? GatewayOverride(ClientInstallEndpointSummary endpoints) =>
        string.Equals(endpoints.EffectiveGatewayBaseUrl, endpoints.PublicApiBaseUrl, StringComparison.OrdinalIgnoreCase)
            ? null : endpoints.EffectiveGatewayBaseUrl;

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
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 ||
            uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains('.') ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.TrimEnd('/').Length != 0)
            throw new InvalidOperationException($"Set the public {name} endpoint to a public HTTPS origin before generating installers.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}

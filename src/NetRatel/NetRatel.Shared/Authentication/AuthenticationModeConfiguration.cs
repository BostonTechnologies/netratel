using Microsoft.Extensions.Configuration;

namespace NetRatel.Shared.Authentication;

/// <summary>Resolves the deployment's interactive account modes consistently in API and Web.</summary>
public static class AuthenticationModeConfiguration
{
    public static string Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration["Authentication:Mode"]?.Trim();
        if (string.IsNullOrWhiteSpace(configured) ||
            string.Equals(configured, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return HasOidcSettings(configuration) ? "Oidc" : "Local";
        }

        if (string.Equals(configured, "Local", StringComparison.OrdinalIgnoreCase)) return "Local";
        if (string.Equals(configured, "Oidc", StringComparison.OrdinalIgnoreCase)) return "Oidc";
        if (string.Equals(configured, "Hybrid", StringComparison.OrdinalIgnoreCase)) return "Hybrid";

        throw new InvalidOperationException("Authentication:Mode must be Local, Oidc, Hybrid, or Auto.");
    }

    public static bool HasOidcSettings(IConfiguration configuration) =>
        HasValues(configuration.GetSection("Authentication:Oidc")) ||
        HasValues(configuration.GetSection("Authentication:Azure")) ||
        HasValues(configuration.GetSection("AzureAd")) ||
        !string.IsNullOrWhiteSpace(configuration["OIDC_CLIENT_SECRET"]) ||
        !string.IsNullOrWhiteSpace(configuration["AZURE_CLIENT_SECRET"]);

    private static bool HasValues(IConfigurationSection section) =>
        section.GetChildren().Any(child => !string.IsNullOrWhiteSpace(child.Value) || HasValues(child));
}

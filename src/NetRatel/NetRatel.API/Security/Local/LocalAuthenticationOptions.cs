namespace NetRatel.API.Security.Local;

public sealed record LocalAuthenticationOptions(
    string Mode,
    bool AllowInsecureLocalhost,
    string CookieName)
{
    public const string Scheme = "NetRatelLocal";
    public const string LocalUserPolicy = "LocalUser";

    public bool SupportsLocalAccounts => Mode is "Local" or "Hybrid";

    public bool UsesOidc => Mode is "Oidc" or "Hybrid";

    public static LocalAuthenticationOptions FromConfiguration(IConfiguration configuration)
    {
        var configuredMode = configuration["Authentication:Mode"]?.Trim();
        var oidc = configuration.GetSection("Authentication:Oidc");
        var hasOidc = !string.IsNullOrWhiteSpace(oidc["Authority"])
            || !string.IsNullOrWhiteSpace(configuration["AzureAd:TenantId"]);
        var mode = string.IsNullOrWhiteSpace(configuredMode) || string.Equals(configuredMode, "Auto", StringComparison.OrdinalIgnoreCase)
            ? (hasOidc ? "Oidc" : "Local")
            : configuredMode;

        if (mode is not ("Local" or "Oidc" or "Hybrid"))
        {
            throw new InvalidOperationException("Authentication:Mode must be Local, Oidc, Hybrid, or Auto.");
        }

        return new LocalAuthenticationOptions(
            mode,
            configuration.GetValue<bool>("Authentication:Local:AllowInsecureLocalhost"),
            configuration["Authentication:Local:CookieName"]?.Trim() is { Length: > 0 } cookieName
                ? cookieName
                : "NetRatel.Local");
    }
}

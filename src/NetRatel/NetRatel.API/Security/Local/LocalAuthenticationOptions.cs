using NetRatel.Shared.Authentication;

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
        var mode = AuthenticationModeConfiguration.Resolve(configuration);

        return new LocalAuthenticationOptions(
            mode,
            configuration.GetValue<bool>("Authentication:Local:AllowInsecureLocalhost"),
            configuration["Authentication:Local:CookieName"]?.Trim() is { Length: > 0 } cookieName
                ? cookieName
                : "NetRatel.Local");
    }
}

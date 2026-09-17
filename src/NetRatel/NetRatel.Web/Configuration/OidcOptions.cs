namespace NetRatel.Web.Configuration;

/// <summary>Configuration for the external OpenID Connect provider used by the Web application.</summary>
public sealed class OidcOptions
{
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ApiScope { get; set; } = string.Empty;
    public string? TokenEndpoint { get; set; }
    public string CallbackPath { get; set; } = "/signin-oidc";
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";
}

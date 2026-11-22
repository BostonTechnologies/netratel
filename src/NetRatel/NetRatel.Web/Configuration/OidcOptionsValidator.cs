using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetRatel.Shared.Authentication;

namespace NetRatel.Web.Configuration;

public sealed class OidcOptionsValidator(IConfiguration configuration, IHostEnvironment environment) : IValidateOptions<OidcOptions>
{
    public ValidateOptionsResult Validate(string? name, OidcOptions options)
    {
        if (AuthenticationModeConfiguration.Resolve(configuration) == "Local")
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();

        if (!options.RequireHttpsMetadata && !environment.IsDevelopment())
        {
            errors.Add(
                "Authentication:Oidc:RequireHttpsMetadata=false is allowed only in the Development environment for isolated disposable test authorities.");
        }

        if (!OidcEndpointSecurity.TryCreateAllowedUri(options.Authority, environment, options.RequireHttpsMetadata, out _))
            errors.Add("Authentication:Oidc:Authority must be an absolute HTTPS URI unless Development explicitly sets RequireHttpsMetadata=false.");

        if (string.IsNullOrWhiteSpace(options.ClientId))
            errors.Add("Authentication:Oidc:ClientId is required.");

        if (string.IsNullOrWhiteSpace(configuration["OIDC_CLIENT_SECRET"]) &&
            string.IsNullOrWhiteSpace(configuration["AZURE_CLIENT_SECRET"]))
        {
            errors.Add("OIDC_CLIENT_SECRET is required.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiScope))
            errors.Add("Authentication:Oidc:ApiScope is required.");

        if (!string.IsNullOrWhiteSpace(options.TokenEndpoint) &&
            !OidcEndpointSecurity.TryCreateAllowedUri(options.TokenEndpoint, environment, options.RequireHttpsMetadata, out _))
        {
            errors.Add(
                "Authentication:Oidc:TokenEndpoint must be an absolute HTTPS URI unless Development explicitly sets RequireHttpsMetadata=false.");
        }

        if (!IsValidPath(options.CallbackPath))
            errors.Add("Authentication:Oidc:CallbackPath must start with '/'.");

        if (!IsValidPath(options.SignedOutCallbackPath))
            errors.Add("Authentication:Oidc:SignedOutCallbackPath must start with '/'.");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }

    private static bool IsValidPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith("/", StringComparison.Ordinal);
}

internal static class OidcEndpointSecurity
{
    internal static bool TryCreateAllowedUri(
        string? value,
        IHostEnvironment environment,
        bool requireHttpsMetadata,
        out Uri? uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return environment.IsDevelopment()
               && !requireHttpsMetadata
               && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }
}

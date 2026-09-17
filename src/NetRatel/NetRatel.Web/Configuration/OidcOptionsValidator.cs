using Microsoft.Extensions.Options;

namespace NetRatel.Web.Configuration;

public sealed class OidcOptionsValidator(IConfiguration configuration) : IValidateOptions<OidcOptions>
{
    public ValidateOptionsResult Validate(string? name, OidcOptions options)
    {
        var errors = new List<string>();

        if (!IsAbsoluteHttpUri(options.Authority))
            errors.Add("Authentication:Oidc:Authority must be an absolute HTTP or HTTPS URI.");

        if (string.IsNullOrWhiteSpace(options.ClientId))
            errors.Add("Authentication:Oidc:ClientId is required.");

        if (string.IsNullOrWhiteSpace(configuration["OIDC_CLIENT_SECRET"]) &&
            string.IsNullOrWhiteSpace(configuration["AZURE_CLIENT_SECRET"]))
        {
            errors.Add("OIDC_CLIENT_SECRET is required.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiScope))
            errors.Add("Authentication:Oidc:ApiScope is required.");

        if (!string.IsNullOrWhiteSpace(options.TokenEndpoint) && !IsAbsoluteHttpUri(options.TokenEndpoint))
            errors.Add("Authentication:Oidc:TokenEndpoint must be an absolute HTTP or HTTPS URI when supplied.");

        if (!IsValidPath(options.CallbackPath))
            errors.Add("Authentication:Oidc:CallbackPath must start with '/'.");

        if (!IsValidPath(options.SignedOutCallbackPath))
            errors.Add("Authentication:Oidc:SignedOutCallbackPath must start with '/'.");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }

    private static bool IsAbsoluteHttpUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    private static bool IsValidPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith("/", StringComparison.Ordinal);
}

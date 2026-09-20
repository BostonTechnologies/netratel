using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record CliConfig(
    string? ApiBaseUrl,
    string? OidcTokenUrl,
    string? OidcClientId,
    string? OidcUsername,
    string? OidcAppPassword,
    string? OidcScope)
{
    public string? IntegrationCredential { get; init; }
    public const string DefaultApiBaseUrl = "https://netratel.example.invalid";
    public const string DefaultScope = "openid profile email";
    public const string ApiBaseUrlEnvironmentVariable = "NETRATEL_CLI_API_BASE_URL";
    public const string OidcTokenUrlEnvironmentVariable = "NETRATEL_CLI_OIDC_TOKEN_URL";
    public const string OidcClientIdEnvironmentVariable = "NETRATEL_CLI_OIDC_CLIENT_ID";
    public const string OidcUsernameEnvironmentVariable = "NETRATEL_CLI_OIDC_USERNAME";
    public const string OidcAppPasswordEnvironmentVariable = "NETRATEL_CLI_OIDC_APP_PASSWORD";
    public const string OidcScopeEnvironmentVariable = "NETRATEL_CLI_OIDC_SCOPE";
    public const string IntegrationCredentialEnvironmentVariable = "NETRATEL_CLI_INTEGRATION_TOKEN";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static CliConfig Empty { get; } = new(null, null, null, null, null, null);

    public static CliConfig FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        return new CliConfig(
            ReadEnvironmentVariable(getEnvironmentVariable, ApiBaseUrlEnvironmentVariable, "BT_NetRatel_API_BASE_URL"),
            ReadEnvironmentVariable(getEnvironmentVariable, OidcTokenUrlEnvironmentVariable, "BT_OIDC_TOKEN_URL"),
            ReadEnvironmentVariable(getEnvironmentVariable, OidcClientIdEnvironmentVariable, "BT_OIDC_CLIENT_ID"),
            ReadEnvironmentVariable(getEnvironmentVariable, OidcUsernameEnvironmentVariable, "BT_OIDC_USERNAME"),
            ReadEnvironmentVariable(getEnvironmentVariable, OidcAppPasswordEnvironmentVariable, "BT_OIDC_APP_PASSWORD"),
            ReadEnvironmentVariable(getEnvironmentVariable, OidcScopeEnvironmentVariable, "BT_OIDC_SCOPE"))
        {
            // The generic name was published with P06; the CLI-specific name
            // takes precedence while retaining that documented secret-injection
            // path for current users.
            IntegrationCredential = Pick(getEnvironmentVariable(IntegrationCredentialEnvironmentVariable), getEnvironmentVariable("NETRATEL_INTEGRATION_TOKEN"))
        };
    }

    public CliConfig Merge(CliConfig next) => this with
    {
        ApiBaseUrl = Pick(next.ApiBaseUrl, ApiBaseUrl),
        OidcTokenUrl = Pick(next.OidcTokenUrl, OidcTokenUrl),
        OidcClientId = Pick(next.OidcClientId, OidcClientId),
        OidcUsername = Pick(next.OidcUsername, OidcUsername),
        OidcAppPassword = Pick(next.OidcAppPassword, OidcAppPassword),
        OidcScope = Pick(next.OidcScope, OidcScope),
        IntegrationCredential = Pick(next.IntegrationCredential, IntegrationCredential)
    };

    public ResolvedCliConfig Resolve()
    {
        static string Require(string? value, string name)
            => string.IsNullOrWhiteSpace(value) ? throw new CliValidationException($"{name} is required.") : value;

        var hasIntegrationCredential = !string.IsNullOrWhiteSpace(IntegrationCredential);
        var hasOidcConfiguration = !string.IsNullOrWhiteSpace(OidcTokenUrl) || !string.IsNullOrWhiteSpace(OidcClientId) ||
            !string.IsNullOrWhiteSpace(OidcUsername) || !string.IsNullOrWhiteSpace(OidcAppPassword) || !string.IsNullOrWhiteSpace(OidcScope);
        if (hasIntegrationCredential && hasOidcConfiguration)
        {
            throw new CliValidationException("Integration credentials cannot be combined with OIDC configuration.");
        }

        var apiBaseUrl = new Uri(string.IsNullOrWhiteSpace(ApiBaseUrl) ? DefaultApiBaseUrl : ApiBaseUrl, UriKind.Absolute);
        if (hasIntegrationCredential)
        {
            return new ResolvedCliConfig(apiBaseUrl, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty)
            {
                AuthenticationMode = CliAuthenticationMode.IntegrationCredential,
                IntegrationCredential = IntegrationCredential
            };
        }

        return new ResolvedCliConfig(
            apiBaseUrl,
            Require(OidcTokenUrl, OidcTokenUrlEnvironmentVariable),
            Require(OidcClientId, OidcClientIdEnvironmentVariable),
            Require(OidcUsername, OidcUsernameEnvironmentVariable),
            Require(OidcAppPassword, OidcAppPasswordEnvironmentVariable),
            string.IsNullOrWhiteSpace(OidcScope) ? DefaultScope : OidcScope);
    }

    private static string? ReadEnvironmentVariable(Func<string, string?> getEnvironmentVariable, string canonicalName, string compatibilityAlias)
        => Pick(getEnvironmentVariable(canonicalName), getEnvironmentVariable(compatibilityAlias));

    private static string? Pick(string? primary, string? fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary;
}

internal enum CliAuthenticationMode
{
    Oidc,
    IntegrationCredential
}

internal sealed record ResolvedCliConfig(
    Uri ApiBaseUrl,
    string OidcTokenUrl,
    string OidcClientId,
    string OidcUsername,
    string OidcAppPassword,
    string OidcScope)
{
    public CliAuthenticationMode AuthenticationMode { get; init; } = CliAuthenticationMode.Oidc;
    public string? IntegrationCredential { get; init; }
}

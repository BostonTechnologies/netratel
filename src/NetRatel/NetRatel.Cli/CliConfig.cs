using NetRatel.AgentClient;
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
    public string? AuthenticationMode { get; init; }
    public string? ProfileName { get; init; }
    public string? Instance { get; init; }
    public string? DefaultTenantId { get; init; }
    public string? CredentialPurpose { get; init; }
    public string? ApprovedOrigin { get; init; }
    public string? ApprovedTarget { get; init; }
    public string? ApprovedTokenEndpoint { get; init; }
    public string? IntegrationCredentialFile { get; init; }
    public string? OidcAppPasswordFile { get; init; }
    public string? ApiM2MClientSecretFile { get; init; }
    public string? ApiM2MTokenUrl { get; init; }
    public string? ApiM2MClientId { get; init; }
    public string? ApiM2MClientSecret { get; init; }
    public string? ApiM2MScope { get; init; }
    public override string ToString() => "CliConfig (credential values omitted)";
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
        => FromAgent(NetRatelConfigurationService.ReadEnvironment(ConfigurationSurface.Cli, getEnvironmentVariable));

    public AgentClientConfiguration ToAgent() => new(ApiBaseUrl, OidcTokenUrl, OidcClientId, OidcUsername, OidcAppPassword, OidcScope)
    {
        IntegrationCredential = IntegrationCredential,
        AuthenticationMode = AuthenticationMode,
        ProfileName = ProfileName,
        Instance = Instance,
        DefaultTenantId = DefaultTenantId,
        CredentialPurpose = CredentialPurpose,
        ApprovedOrigin = ApprovedOrigin,
        ApprovedTarget = ApprovedTarget,
        ApprovedTokenEndpoint = ApprovedTokenEndpoint,
        IntegrationCredentialFile = IntegrationCredentialFile,
        OidcAppPasswordFile = OidcAppPasswordFile,
        ApiM2MClientSecretFile = ApiM2MClientSecretFile,
        ApiM2MTokenUrl = ApiM2MTokenUrl,
        ApiM2MClientId = ApiM2MClientId,
        ApiM2MClientSecret = ApiM2MClientSecret,
        ApiM2MScope = ApiM2MScope,
    };

    public static CliConfig FromAgent(AgentClientConfiguration config) => new(config.ApiBaseUrl, config.OidcTokenUrl, config.OidcClientId, config.OidcUsername, config.OidcAppPassword, config.OidcScope)
    {
        IntegrationCredential = config.IntegrationCredential,
        AuthenticationMode = config.AuthenticationMode,
        ProfileName = config.ProfileName,
        Instance = config.Instance,
        DefaultTenantId = config.DefaultTenantId,
        CredentialPurpose = config.CredentialPurpose,
        ApprovedOrigin = config.ApprovedOrigin,
        ApprovedTarget = config.ApprovedTarget,
        ApprovedTokenEndpoint = config.ApprovedTokenEndpoint,
        IntegrationCredentialFile = config.IntegrationCredentialFile,
        OidcAppPasswordFile = config.OidcAppPasswordFile,
        ApiM2MClientSecretFile = config.ApiM2MClientSecretFile,
        ApiM2MTokenUrl = config.ApiM2MTokenUrl,
        ApiM2MClientId = config.ApiM2MClientId,
        ApiM2MClientSecret = config.ApiM2MClientSecret,
        ApiM2MScope = config.ApiM2MScope,
    };

    public CliConfig Merge(CliConfig next) => FromAgent(ToAgent().Merge(next.ToAgent()));

    public ResolvedCliConfig Resolve()
    {
        try
        {
            var resolved = ToAgent().Resolve();
            return new ResolvedCliConfig(resolved.ApiBaseUrl, resolved.OidcTokenUrl, resolved.OidcClientId, resolved.OidcUsername, resolved.OidcAppPassword, resolved.OidcScope)
            {
                AuthenticationMode = (CliAuthenticationMode)resolved.AuthenticationMode,
                IntegrationCredential = resolved.IntegrationCredential,
                ApiM2MTokenUrl = resolved.ApiM2MTokenUrl,
                ApiM2MClientId = resolved.ApiM2MClientId,
                ApiM2MClientSecret = resolved.ApiM2MClientSecret,
                ApiM2MScope = resolved.ApiM2MScope
            };
        }
        catch (AgentClientValidationException exception) { throw new CliValidationException(exception.Message); }
    }

    private static string? ReadEnvironmentVariable(Func<string, string?> getEnvironmentVariable, string canonicalName, string compatibilityAlias)
        => Pick(getEnvironmentVariable(canonicalName), getEnvironmentVariable(compatibilityAlias));

    private static string? Pick(string? primary, string? fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary;
}

internal enum CliAuthenticationMode
{
    Oidc,
    IntegrationCredential,
    ApiM2MClientSecret
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
    public string? ApiM2MTokenUrl { get; init; }
    public string? ApiM2MClientId { get; init; }
    public string? ApiM2MClientSecret { get; init; }
    public string? ApiM2MScope { get; init; }
    public override string ToString() => $"ResolvedCliConfig {{ ApiBaseUrl = {ApiBaseUrl}, AuthenticationMode = {AuthenticationMode} }}";
}

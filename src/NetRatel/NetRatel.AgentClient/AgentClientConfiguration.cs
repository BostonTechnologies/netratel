using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.AgentClient;

public sealed record AgentClientConfiguration(
    string? ApiBaseUrl,
    string? OidcTokenUrl,
    string? OidcClientId,
    string? OidcUsername,
    string? OidcAppPassword,
    string? OidcScope)
{
    /// <summary>
    /// Optional API-owned M2M token configuration for an isolated MCP host.
    /// When all four values are present, the host uses the NetRatel API's
    /// standard client-credentials endpoint instead of the legacy Oidc
    /// application-password flow. These properties are additive so existing
    /// persisted six-field configurations remain wire-compatible.
    /// </summary>
    public string? ApiM2MTokenUrl { get; init; }
    public string? ApiM2MClientId { get; init; }
    public string? ApiM2MClientSecret { get; init; }
    public string? ApiM2MScope { get; init; }

    /// <summary>
    /// A one-time-revealed, API-purpose integration credential. This is an
    /// explicit alternative to OIDC for a local CLI or isolated stdio MCP
    /// host; it is never discovered from an unrelated credential source.
    /// </summary>
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

    // Records containing credentials must never print their generated property list.
    public override string ToString() => "AgentClientConfiguration (credential values omitted)";

    public const string DefaultApiBaseUrl = "https://netratel.example.invalid";
    public const string DefaultScope = "openid profile email";
    public const string ApiBaseUrlEnvironmentVariable = "NETRATEL_AGENT_CLIENT_API_BASE_URL";
    public const string OidcTokenUrlEnvironmentVariable = "NETRATEL_AGENT_CLIENT_OIDC_TOKEN_URL";
    public const string OidcClientIdEnvironmentVariable = "NETRATEL_AGENT_CLIENT_OIDC_CLIENT_ID";
    public const string OidcUsernameEnvironmentVariable = "NETRATEL_AGENT_CLIENT_OIDC_USERNAME";
    public const string OidcAppPasswordEnvironmentVariable = "NETRATEL_AGENT_CLIENT_OIDC_APP_PASSWORD";
    public const string OidcScopeEnvironmentVariable = "NETRATEL_AGENT_CLIENT_OIDC_SCOPE";
    public const string IntegrationCredentialEnvironmentVariable = "NETRATEL_AGENT_CLIENT_INTEGRATION_TOKEN";
    public static AgentClientConfiguration Empty { get; } = new(null, null, null, null, null, null);
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AgentClientConfiguration FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        return NetRatelConfigurationService.ReadEnvironment(ConfigurationSurface.AgentClient, getEnvironmentVariable);
    }

    public AgentClientConfiguration Merge(AgentClientConfiguration next) => this with
    {
        ApiBaseUrl = next.ApiBaseUrl ?? ApiBaseUrl,
        OidcTokenUrl = next.OidcTokenUrl ?? OidcTokenUrl,
        OidcClientId = next.OidcClientId ?? OidcClientId,
        OidcUsername = next.OidcUsername ?? OidcUsername,
        OidcAppPassword = next.OidcAppPassword ?? OidcAppPassword,
        OidcScope = next.OidcScope ?? OidcScope,
        ApiM2MTokenUrl = next.ApiM2MTokenUrl ?? ApiM2MTokenUrl,
        ApiM2MClientId = next.ApiM2MClientId ?? ApiM2MClientId,
        ApiM2MClientSecret = next.ApiM2MClientSecret ?? ApiM2MClientSecret,
        ApiM2MScope = next.ApiM2MScope ?? ApiM2MScope,
        IntegrationCredential = next.IntegrationCredential ?? IntegrationCredential,
        AuthenticationMode = next.AuthenticationMode ?? AuthenticationMode,
        ProfileName = next.ProfileName ?? ProfileName,
        Instance = next.Instance ?? Instance,
        DefaultTenantId = next.DefaultTenantId ?? DefaultTenantId,
        CredentialPurpose = next.CredentialPurpose ?? CredentialPurpose,
        ApprovedOrigin = next.ApprovedOrigin ?? ApprovedOrigin,
        ApprovedTarget = next.ApprovedTarget ?? ApprovedTarget,
        ApprovedTokenEndpoint = next.ApprovedTokenEndpoint ?? ApprovedTokenEndpoint,
        IntegrationCredentialFile = next.IntegrationCredentialFile ?? IntegrationCredentialFile,
        OidcAppPasswordFile = next.OidcAppPasswordFile ?? OidcAppPasswordFile,
        ApiM2MClientSecretFile = next.ApiM2MClientSecretFile ?? ApiM2MClientSecretFile
    };

    public ResolvedAgentClientConfiguration Resolve()
    {
        var configuration = NetRatelConfigurationService.ResolveSecretReferences(this);
        var result = new AgentClientConfigurationValidator().Validate(null, configuration);
        if (result.Failed)
            throw new AgentClientValidationException(string.Join(" ", result.Failures));

        var apiBaseUrl = new Uri(string.IsNullOrWhiteSpace(configuration.ApiBaseUrl) ? DefaultApiBaseUrl : configuration.ApiBaseUrl, UriKind.Absolute);
        var mode = NetRatelConfigurationService.AuthenticationModeOf(configuration);
        return new ResolvedAgentClientConfiguration(apiBaseUrl,
            configuration.OidcTokenUrl ?? string.Empty, configuration.OidcClientId ?? string.Empty,
            configuration.OidcUsername ?? string.Empty, configuration.OidcAppPassword ?? string.Empty,
            string.IsNullOrWhiteSpace(configuration.OidcScope) ? DefaultScope : configuration.OidcScope)
        {
            AuthenticationMode = mode,
            IntegrationCredential = configuration.IntegrationCredential,
            ApiM2MTokenUrl = configuration.ApiM2MTokenUrl,
            ApiM2MClientId = configuration.ApiM2MClientId,
            ApiM2MClientSecret = configuration.ApiM2MClientSecret,
            ApiM2MScope = configuration.ApiM2MScope
        };
    }

    private static string? ReadEnvironmentVariable(Func<string, string?> getEnvironmentVariable, string canonicalName, string compatibilityAlias)
        => Pick(getEnvironmentVariable(canonicalName), getEnvironmentVariable(compatibilityAlias));

    private static string? Pick(string? primary, string? fallback) => string.IsNullOrWhiteSpace(primary) ? fallback : primary;
}

public enum AgentClientAuthenticationMode
{
    Oidc,
    IntegrationCredential,
    ApiM2MClientSecret
}

public sealed record ResolvedAgentClientConfiguration(Uri ApiBaseUrl, string OidcTokenUrl, string OidcClientId, string OidcUsername, string OidcAppPassword, string OidcScope)
{
    public AgentClientAuthenticationMode AuthenticationMode { get; init; } = AgentClientAuthenticationMode.Oidc;
    public string? IntegrationCredential { get; init; }
    public string? ApiM2MTokenUrl { get; init; }
    public string? ApiM2MClientId { get; init; }
    public string? ApiM2MClientSecret { get; init; }
    public string? ApiM2MScope { get; init; }
    public override string ToString() => $"ResolvedAgentClientConfiguration {{ ApiBaseUrl = {ApiBaseUrl}, AuthenticationMode = {AuthenticationMode} }}";
}

public sealed class AgentClientConfigurationStore
{
    private readonly bool _readOnly;
    private readonly bool _hasSelectedFile;
    public AgentClientConfigurationStore(string? path = null, bool readOnly = false)
    {
        Path = path ?? DefaultPath;
        _readOnly = readOnly;
        _hasSelectedFile = path is not null || !readOnly;
    }
    public string Path { get; }
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "netratel", "cli.json");

    public AgentClientConfiguration Load() => !_hasSelectedFile || !File.Exists(Path)
        ? AgentClientConfiguration.Empty
        : NetRatelConfigurationService.ReadConfiguration(Path);

    public void Save(AgentClientConfiguration configuration)
    {
        if (_readOnly) throw new AgentClientValidationException("Configuration is externally managed and has no writable selected file. Export a reviewed profile through explicit setup; deployment settings require recreation/restart.");
        var existed = File.Exists(Path);
        var before = existed ? ConfigurationFileStorage.Hash(Path) : string.Empty;
        var previous = Load();
        if (NetRatelConfigurationService.HasSecrets(previous) && !NetRatelConfigurationService.Equivalent(previous.ApiBaseUrl, configuration.ApiBaseUrl))
            throw new AgentClientValidationException("Changing a credential's target requires explicit reauthorization/rebind through setup. Existing credentials will not be sent to a new URL.");
        if ((!string.IsNullOrWhiteSpace(previous.OidcAppPassword) || !string.IsNullOrWhiteSpace(previous.OidcAppPasswordFile)) &&
            !NetRatelConfigurationService.Equivalent(previous.OidcTokenUrl, configuration.OidcTokenUrl))
            throw new AgentClientValidationException("Changing a credential's token endpoint requires explicit reauthorization/rebind through setup.");
        ConfigurationFileStorage.WriteAtomic(Path, JsonSerializer.Serialize(configuration, AgentClientConfiguration.JsonOptions) + Environment.NewLine,
            replace: existed, expectedHash: before);
    }

}

public static class AgentClientConfigurationResolver
{
    /// <summary>
    /// Loads exactly one deployment-owned configuration file for an isolated MCP
    /// host. Unlike <see cref="Load"/>, ambient configuration variables are
    /// deliberately ignored so they cannot redirect a host or replace its app
    /// credentials.
    /// </summary>
    public static AgentClientConfiguration LoadIsolated(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new AgentClientValidationException("NETRATEL_MCP_CONFIG is required for isolated MCP configuration.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new AgentClientValidationException("NETRATEL_MCP_CONFIG must be an absolute path.");
        }

        if (!File.Exists(path))
        {
            throw new AgentClientValidationException("NETRATEL_MCP_CONFIG does not exist.");
        }

        return new AgentClientConfigurationStore(path).Load();
    }

    public static AgentClientConfiguration LoadMcpIsolatedConfiguration(Func<string, string?> getEnvironmentVariable)
    {
        var path = GetMcpConfigurationPath(getEnvironmentVariable);
        // Preserve the actionable legacy error when neither isolated input is supplied.
        var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.StdioMcp, path, GetEnvironmentVariable: getEnvironmentVariable));
        if (effective.Configuration == AgentClientConfiguration.Empty)
            throw new AgentClientValidationException("NETRATEL_MCP_CONFIG is required unless dedicated NetRatel:Mcp:Outbound environment settings are supplied.");
        return effective.Configuration;
    }

    public static string? GetMcpConfigurationPath(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var selected = getEnvironmentVariable("NETRATEL_MCP_CONFIG") ?? getEnvironmentVariable("NetRatel_MCP_CONFIG");
        return string.IsNullOrWhiteSpace(selected) ? null : selected;
    }

    public static AgentClientConfiguration Load(string? configPath = null)
    {
        return new NetRatelConfigurationService().Load(new(ConfigurationSurface.AgentClient, configPath)).Configuration;
    }

    private static string? Pick(string? primary, string? fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    public static object Redact(AgentClientConfiguration config) => new
    {
        config.ApiBaseUrl,
        config.OidcTokenUrl,
        config.OidcClientId,
        config.OidcUsername,
        oidcAppPassword = string.IsNullOrWhiteSpace(config.OidcAppPassword) ? null : "***REDACTED***",
        config.OidcScope,
        config.ApiM2MTokenUrl,
        config.ApiM2MClientId,
        apiM2MClientSecret = string.IsNullOrWhiteSpace(config.ApiM2MClientSecret) ? null : "***REDACTED***",
        config.ApiM2MScope,
        integrationCredential = string.IsNullOrWhiteSpace(config.IntegrationCredential) ? null : "***REDACTED***"
    };
}

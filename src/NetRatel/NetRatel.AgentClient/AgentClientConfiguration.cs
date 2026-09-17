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

    public const string DefaultApiBaseUrl = "https://netratel.example.invalid";
    public const string DefaultScope = "openid profile email";
    public const string ApiBaseUrlEnvironmentVariable = "NETRATEL_AGENT_CLIENT_API_BASE_URL";
    public const string OidcTokenUrlEnvironmentVariable = "NETRATEL_AGENT_CLIENT_OIDC_TOKEN_URL";
    public const string OidcClientIdEnvironmentVariable = "NETRATEL_AGENT_CLIENT_OIDC_CLIENT_ID";
    public const string OidcUsernameEnvironmentVariable = "NETRATEL_AGENT_CLIENT_OIDC_USERNAME";
    public const string OidcAppPasswordEnvironmentVariable = "NETRATEL_AGENT_CLIENT_OIDC_APP_PASSWORD";
    public const string OidcScopeEnvironmentVariable = "NETRATEL_AGENT_CLIENT_OIDC_SCOPE";
    public static AgentClientConfiguration Empty { get; } = new(null, null, null, null, null, null);
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AgentClientConfiguration FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        return new AgentClientConfiguration(
            ReadEnvironmentVariable(getEnvironmentVariable, ApiBaseUrlEnvironmentVariable, "BT_NetRatel_API_BASE_URL"),
            ReadEnvironmentVariable(getEnvironmentVariable, OidcTokenUrlEnvironmentVariable, "BT_OIDC_TOKEN_URL"),
            ReadEnvironmentVariable(getEnvironmentVariable, OidcClientIdEnvironmentVariable, "BT_OIDC_CLIENT_ID"),
            ReadEnvironmentVariable(getEnvironmentVariable, OidcUsernameEnvironmentVariable, "BT_OIDC_USERNAME"),
            ReadEnvironmentVariable(getEnvironmentVariable, OidcAppPasswordEnvironmentVariable, "BT_OIDC_APP_PASSWORD"),
            ReadEnvironmentVariable(getEnvironmentVariable, OidcScopeEnvironmentVariable, "BT_OIDC_SCOPE"));
    }

    public AgentClientConfiguration Merge(AgentClientConfiguration next) => this with
    {
        ApiBaseUrl = Pick(next.ApiBaseUrl, ApiBaseUrl),
        OidcTokenUrl = Pick(next.OidcTokenUrl, OidcTokenUrl),
        OidcClientId = Pick(next.OidcClientId, OidcClientId),
        OidcUsername = Pick(next.OidcUsername, OidcUsername),
        OidcAppPassword = Pick(next.OidcAppPassword, OidcAppPassword),
        OidcScope = Pick(next.OidcScope, OidcScope),
        ApiM2MTokenUrl = Pick(next.ApiM2MTokenUrl, ApiM2MTokenUrl),
        ApiM2MClientId = Pick(next.ApiM2MClientId, ApiM2MClientId),
        ApiM2MClientSecret = Pick(next.ApiM2MClientSecret, ApiM2MClientSecret),
        ApiM2MScope = Pick(next.ApiM2MScope, ApiM2MScope)
    };

    public ResolvedAgentClientConfiguration Resolve()
    {
        static string Require(string? value, string name) => string.IsNullOrWhiteSpace(value)
            ? throw new AgentClientValidationException($"{name} is required.")
            : value;

        return new ResolvedAgentClientConfiguration(
            new Uri(string.IsNullOrWhiteSpace(ApiBaseUrl) ? DefaultApiBaseUrl : ApiBaseUrl, UriKind.Absolute),
            Require(OidcTokenUrl, OidcTokenUrlEnvironmentVariable),
            Require(OidcClientId, OidcClientIdEnvironmentVariable),
            Require(OidcUsername, OidcUsernameEnvironmentVariable),
            Require(OidcAppPassword, OidcAppPasswordEnvironmentVariable),
            string.IsNullOrWhiteSpace(OidcScope) ? DefaultScope : OidcScope);
    }

    private static string? ReadEnvironmentVariable(Func<string, string?> getEnvironmentVariable, string canonicalName, string compatibilityAlias)
        => Pick(getEnvironmentVariable(canonicalName), getEnvironmentVariable(compatibilityAlias));

    private static string? Pick(string? primary, string? fallback) => string.IsNullOrWhiteSpace(primary) ? fallback : primary;
}

public sealed record ResolvedAgentClientConfiguration(Uri ApiBaseUrl, string OidcTokenUrl, string OidcClientId, string OidcUsername, string OidcAppPassword, string OidcScope);

public sealed class AgentClientConfigurationStore
{
    public AgentClientConfigurationStore(string? path = null) => Path = path ?? DefaultPath;
    public string Path { get; }
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "netratel", "cli.json");

    public AgentClientConfiguration Load() => !File.Exists(Path)
        ? AgentClientConfiguration.Empty
        : JsonSerializer.Deserialize<AgentClientConfiguration>(File.ReadAllText(Path), AgentClientConfiguration.JsonOptions) ?? AgentClientConfiguration.Empty;

    public void Save(AgentClientConfiguration configuration)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(Path, JsonSerializer.Serialize(configuration, AgentClientConfiguration.JsonOptions) + Environment.NewLine);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* Best effort on filesystems without Unix mode support. */ }
        }
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
        return LoadIsolated(GetMcpConfigurationPath(getEnvironmentVariable));
    }

    public static string? GetMcpConfigurationPath(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        return Pick(
            getEnvironmentVariable("NETRATEL_MCP_CONFIG"),
            getEnvironmentVariable("NetRatel_MCP_CONFIG"));
    }

    public static AgentClientConfiguration Load(string? configPath = null)
    {
        var file = new AgentClientConfigurationStore(configPath).Load();
        var environment = AgentClientConfiguration.FromEnvironment(Environment.GetEnvironmentVariable);
        return file.Merge(environment);
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
        config.ApiM2MScope
    };
}

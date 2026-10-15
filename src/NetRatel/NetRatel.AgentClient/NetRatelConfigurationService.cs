using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace NetRatel.AgentClient;

public enum ConfigurationSurface { Cli, AgentClient, StdioMcp, HttpMcpOutbound, HttpMcpClient }

public sealed record ConfigurationRequest(
    ConfigurationSurface Surface,
    string? ConfigurationPath = null,
    string? ProfileName = null,
    AgentClientConfiguration? CommandOptions = null,
    Func<string, string?>? GetEnvironmentVariable = null);

public sealed record ConfigurationSettingSource(string Source, bool ExternallyManaged, bool IsSecret = false, string? Reference = null);
public sealed record ConfigurationValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public int ExitCode => IsValid ? 0 : 2;
}
public sealed record ConfigurationDiagnostic(
    int SchemaVersion, bool IsValid, ConfigurationSurface Surface, string? ProfileName, string? ConfigurationPath,
    object Effective, IReadOnlyDictionary<string, ConfigurationSettingSource> Sources, IReadOnlyList<string> Errors);

public sealed record EffectiveAgentClientConfiguration(
    AgentClientConfiguration Configuration, ConfigurationSurface Surface, string? ConfigurationPath,
    string? ProfileName, IReadOnlyDictionary<string, ConfigurationSettingSource> Sources)
{
    public ConfigurationValidationResult Validate()
    {
        var result = new AgentClientConfigurationValidator(Surface).Validate(null, Configuration);
        return new(!result.Failed, result.Failures?.ToArray() ?? []);
    }

    public ConfigurationDiagnostic Diagnostics()
    {
        var validation = Validate();
        return new(1, validation.IsValid, Surface, ProfileName, ConfigurationPath,
            NetRatelConfigurationService.Redact(Configuration), Sources, validation.Errors);
    }
}

/// <summary>The one headless engine used by runtime, setup, config validation and diagnostics.</summary>
public sealed class NetRatelConfigurationService
{
    private static readonly PropertyInfo[] Properties = typeof(AgentClientConfiguration).GetProperties()
        .Where(property => property.PropertyType == typeof(string) && property.SetMethod is not null).ToArray();
    private static readonly HashSet<string> SecretNames = [nameof(AgentClientConfiguration.IntegrationCredential), nameof(AgentClientConfiguration.OidcAppPassword), nameof(AgentClientConfiguration.ApiM2MClientSecret)];

    /// <summary>Preserves provider boundaries when a serving host uses typed .NET JSON/environment sections.</summary>
    public EffectiveAgentClientConfiguration Load(ConfigurationRequest request, IConfiguration configuration, bool allowCreate = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration is not IConfigurationRoot root)
            return Load(request with { GetEnvironmentVariable = key => configuration[key] }, allowCreate);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var sources = new Dictionary<string, ConfigurationSettingSource>(StringComparer.Ordinal);
        string? selectedMcpPath = null;
        var hasMcpPath = false;
        foreach (var provider in root.Providers)
        {
            var layerSources = new Dictionary<string, ConfigurationSettingSource>(StringComparer.Ordinal);
            string? Read(string key)
                => provider.TryGet(key, out var value) || provider.TryGet(key.Replace("__", ":", StringComparison.Ordinal), out value) ? value ?? string.Empty : null;
            var layer = ReadEnvironment(request.Surface, Read, layerSources);
            var previous = FromValues(values);
            var external = provider.GetType().Name.Contains("Environment", StringComparison.Ordinal) || provider.GetType().Name.Contains("CommandLine", StringComparison.Ordinal);
            foreach (var key in layerSources.Keys.ToArray())
                layerSources[key] = layerSources[key] with { Source = provider.GetType().Name + ":" + layerSources[key].Source, ExternallyManaged = external };
            Overlay(values, Values(layer), layerSources, sources);
            EnsureCredentialBindings(previous, FromValues(values));
            if ((Read("NETRATEL_MCP_CONFIG") ?? Read("NetRatel_MCP_CONFIG")) is { } path)
            {
                selectedMcpPath = path;
                hasMcpPath = true;
            }
        }
        var canonical = Properties.Where(property => values.ContainsKey(property.Name)).ToDictionary(
            property => EnvironmentNames(request.Surface, property.Name)[0], property => values[property.Name], StringComparer.Ordinal);
        string? EffectiveEnvironment(string key)
            => canonical.TryGetValue(key, out var value) ? value : key == "NETRATEL_MCP_CONFIG" && hasMcpPath ? selectedMcpPath : null;
        var effective = Load(request with { GetEnvironmentVariable = EffectiveEnvironment }, allowCreate);
        EnsureCredentialBindings(FromValues(values), effective.Configuration);
        var effectiveSources = effective.Sources.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var pair in sources)
        {
            if (effectiveSources.TryGetValue(pair.Key, out var final) && final.Source != "command") effectiveSources[pair.Key] = pair.Value;
            if (pair.Key.EndsWith("File", StringComparison.Ordinal)) effectiveSources[pair.Key[..^4]] = pair.Value with { IsSecret = true };
        }
        return effective with { Sources = new ReadOnlyDictionary<string, ConfigurationSettingSource>(effectiveSources) };
    }

    public EffectiveAgentClientConfiguration Load(ConfigurationRequest request, bool allowCreate = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        var environment = request.GetEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var sources = new Dictionary<string, ConfigurationSettingSource>(StringComparer.Ordinal);
        var environmentConfiguration = ReadEnvironment(request.Surface, environment, sources);
        var profileName = request.ProfileName ?? environmentConfiguration.ProfileName;
        if (profileName is not null) ValidateProfileName(profileName);
        var configurationPathSpecified = request.ConfigurationPath is not null;
        var path = request.ConfigurationPath;
        if (request.Surface == ConfigurationSurface.HttpMcpOutbound && path == string.Empty) path = null;
        var explicitlySelected = path is not null || profileName is not null;
        if (path is null && profileName is not null) path = ProfilePath(request.Surface, profileName, environment);
        if (path is null && !configurationPathSpecified && request.Surface is ConfigurationSurface.StdioMcp or ConfigurationSurface.HttpMcpOutbound)
        {
            path = AgentClientConfigurationResolver.GetMcpConfigurationPath(environment);
            explicitlySelected = path is not null;
        }
        if (path is null && request.Surface is ConfigurationSurface.Cli or ConfigurationSurface.AgentClient)
            path = AgentClientConfigurationStore.DefaultPath;

        var file = AgentClientConfiguration.Empty;
        if (path is not null)
        {
            if (request.Surface is ConfigurationSurface.StdioMcp or ConfigurationSurface.HttpMcpOutbound && !Path.IsPathFullyQualified(path))
                throw new AgentClientValidationException("Isolated MCP configuration/profile paths must be absolute.");
            path = ExpandPath(path);
            if (explicitlySelected && !File.Exists(path) && !allowCreate)
                throw new AgentClientValidationException("The explicitly selected configuration/profile file does not exist; ambient credentials will not be used.");
            if (File.Exists(path)) file = ReadConfiguration(path);
        }
        if (file.ProfileName is not null && profileName is not null && !string.Equals(file.ProfileName, profileName, StringComparison.Ordinal))
            throw new AgentClientValidationException("The selected profile name does not match the configuration file.");
        if (file.CredentialPurpose is not null && !string.Equals(file.CredentialPurpose, PurposeFor(request.Surface), StringComparison.Ordinal))
            throw new AgentClientValidationException("The selected profile credential purpose is not valid for this surface. Use its own isolated profile.");

        var values = Values(file);
        var fileManaged = path is not null && File.Exists(path) && !ConfigurationFileStorage.IsWritable(path);
        var effectiveSources = values.ToDictionary(pair => pair.Key,
            pair => new ConfigurationSettingSource("file:" + path, fileManaged || pair.Key.EndsWith("File", StringComparison.Ordinal) &&
                pair.Value is not null && File.Exists(pair.Value) && !ConfigurationFileStorage.IsWritable(pair.Value),
                IsSecret(pair.Key), pair.Key.EndsWith("File", StringComparison.Ordinal) ? pair.Value : null), StringComparer.Ordinal);
        Overlay(values, Values(environmentConfiguration), sources, effectiveSources);
        if (request.CommandOptions is not null)
        {
            var command = Values(request.CommandOptions);
            if (command.Keys.Any(key => IsSecret(key) || key.EndsWith("File", StringComparison.Ordinal)))
                throw new AgentClientValidationException("Secrets and secret-file references are not accepted in command arguments; use a protected file or dedicated environment setting.");
            Overlay(values, command, command.ToDictionary(pair => pair.Key, _ => new ConfigurationSettingSource("command", false), StringComparer.Ordinal), effectiveSources);
        }
        var configuration = FromValues(values);
        // Legacy files are also bound to their original endpoint when a higher source edits the URL.
        EnsureCredentialBindings(file, configuration);
        configuration = ResolveSecretReferences(configuration);
        foreach (var secret in SecretNames)
            if (effectiveSources.TryGetValue(secret + "File", out var reference))
                effectiveSources[secret] = reference with { IsSecret = true };
        effectiveSources.TryAdd(nameof(AgentClientConfiguration.ApiBaseUrl), new("built-in default", false));
        effectiveSources.TryAdd(nameof(AgentClientConfiguration.AuthenticationMode), new("derived from selected credentials", false));
        if (AuthenticationModeOf(configuration) == AgentClientAuthenticationMode.Oidc)
            effectiveSources.TryAdd(nameof(AgentClientConfiguration.OidcScope), new("built-in default", false));
        return new(configuration, request.Surface, path, profileName ?? file.ProfileName,
            new ReadOnlyDictionary<string, ConfigurationSettingSource>(effectiveSources));
    }

    public static AgentClientConfiguration ReadEnvironment(ConfigurationSurface surface, Func<string, string?> environment,
        IDictionary<string, ConfigurationSettingSource>? sources = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in Properties)
        {
            var names = EnvironmentNames(surface, property.Name);
            foreach (var name in names)
            {
                var value = environment(name);
                if (value is null) continue;
                values[property.Name] = value; // A present empty value clears the lower source.
                if (sources is not null) sources[property.Name] = new(name, true, IsSecret(property.Name), property.Name.EndsWith("File", StringComparison.Ordinal) ? value : null);
                break;
            }
        }
        return FromValues(values);
    }

    public static IReadOnlyList<string> EnvironmentNames(ConfigurationSurface surface, string property)
    {
        var section = surface switch
        {
            ConfigurationSurface.Cli => "NetRatel__Cli__",
            ConfigurationSurface.AgentClient => "NetRatel__AgentClient__",
            ConfigurationSurface.HttpMcpClient => "NetRatel__Mcp__Client__",
            _ => "NetRatel__Mcp__Outbound__"
        };
        var compact = surface switch
        {
            ConfigurationSurface.Cli => "NETRATEL_CLI_",
            ConfigurationSurface.AgentClient => "NETRATEL_AGENT_CLIENT_",
            ConfigurationSurface.HttpMcpClient => "NETRATEL_MCP_CLIENT_",
            _ => "NETRATEL_MCP_OUTBOUND_"
        };
        var suffix = property switch
        {
            nameof(AgentClientConfiguration.IntegrationCredential) => "INTEGRATION_TOKEN",
            nameof(AgentClientConfiguration.IntegrationCredentialFile) => "INTEGRATION_TOKEN_FILE",
            _ => SnakeCase(property)
        };
        var names = new List<string> { section + property, section.Replace("__", ":") + property, compact + suffix };
        if (surface == ConfigurationSurface.StdioMcp && property == nameof(AgentClientConfiguration.Instance))
            names.Add("NETRATEL_MCP_INSTANCE");
        if (property.EndsWith("File", StringComparison.Ordinal))
            names.Insert(1, section + property[..^4] + "_FILE");
        if (surface is ConfigurationSurface.Cli or ConfigurationSurface.AgentClient)
        {
            var legacy = property switch
            {
                nameof(AgentClientConfiguration.ApiBaseUrl) => "BT_NetRatel_API_BASE_URL",
                nameof(AgentClientConfiguration.OidcTokenUrl) => "BT_OIDC_TOKEN_URL",
                nameof(AgentClientConfiguration.OidcClientId) => "BT_OIDC_CLIENT_ID",
                nameof(AgentClientConfiguration.OidcUsername) => "BT_OIDC_USERNAME",
                nameof(AgentClientConfiguration.OidcAppPassword) => "BT_OIDC_APP_PASSWORD",
                nameof(AgentClientConfiguration.OidcScope) => "BT_OIDC_SCOPE",
                _ => null
            };
            if (legacy is not null) names.Add(legacy);
            if (surface == ConfigurationSurface.Cli && property == nameof(AgentClientConfiguration.IntegrationCredential)) names.Add("NETRATEL_INTEGRATION_TOKEN");
            if (surface == ConfigurationSurface.Cli && property == nameof(AgentClientConfiguration.IntegrationCredentialFile)) names.Add("NETRATEL_INTEGRATION_TOKEN_FILE");
        }
        return names;
    }

    public void SaveProfile(string path, AgentClientConfiguration configuration, ConfigurationSurface surface,
        bool replace = false, bool explicitRebind = false, string? expectedExistingHash = null)
    {
        path = ExpandPath(path);
        ConfigurationFileStorage.EnsureSafePath(path);
        var existing = File.Exists(path) ? ReadConfiguration(path) : null;
        var expectedHash = expectedExistingHash ?? (existing is null ? null : ConfigurationFileStorage.Hash(path));
        if (expectedExistingHash is not null && expectedExistingHash != (existing is null ? string.Empty : ConfigurationFileStorage.Hash(path)))
            throw new AgentClientValidationException("The profile changed after review. Reload before saving.");
        if (existing is not null && !replace)
            throw new AgentClientValidationException("The profile already exists. Review and explicitly confirm replacement.");
        if (configuration.CredentialPurpose is not null && configuration.CredentialPurpose != PurposeFor(surface) ||
            existing?.CredentialPurpose is not null && existing.CredentialPurpose != PurposeFor(surface))
            throw new AgentClientValidationException("A credential cannot be imported into a profile with a different purpose. Authorize a separate identity for this surface.");
        if (existing is not null && HasSecrets(existing) && !Equivalent(existing.ApprovedTarget ?? existing.ApiBaseUrl, configuration.ApiBaseUrl) && !explicitRebind)
            throw new AgentClientValidationException("A target change requires explicit reauthorization/rebind before replacing a credential-bound profile.");
        if (!IsHttps(configuration.ApiBaseUrl)) throw new AgentClientValidationException("Profile target must be an absolute HTTPS URL without user information, query or fragment.");
        configuration = configuration with
        {
            CredentialPurpose = PurposeFor(surface),
            ApprovedOrigin = new Uri(configuration.ApiBaseUrl!).GetLeftPart(UriPartial.Authority),
            ApprovedTarget = new Uri(configuration.ApiBaseUrl!).AbsoluteUri.TrimEnd('/'),
            ApprovedTokenEndpoint = AuthenticationModeOf(configuration) switch
            {
                AgentClientAuthenticationMode.Oidc => configuration.OidcTokenUrl,
                AgentClientAuthenticationMode.ApiM2MClientSecret => configuration.ApiM2MTokenUrl,
                _ => null
            }
        };
        var validation = new AgentClientConfigurationValidator(surface).Validate(null, ResolveSecretReferences(configuration));
        if (validation.Failed) throw new AgentClientValidationException(string.Join(" ", validation.Failures));
        var createdSecrets = new List<string>();
        try
        {
            var values = Values(configuration);
            foreach (var secretName in SecretNames)
            {
                if (!values.TryGetValue(secretName, out var secret) || string.IsNullOrWhiteSpace(secret)) continue;
                var secretPath = path + "." + SnakeCase(secretName).ToLowerInvariant() + "." + Guid.NewGuid().ToString("N") + ".secret";
                ConfigurationFileStorage.WriteAtomic(secretPath, secret + "\n");
                createdSecrets.Add(secretPath);
                values.Remove(secretName);
                values[secretName + "File"] = secretPath;
            }
            ConfigurationFileStorage.WriteAtomic(path,
                JsonSerializer.Serialize(FromValues(values), AgentClientConfiguration.JsonOptions) + Environment.NewLine, replace, expectedHash);
        }
        catch
        {
            foreach (var secretPath in createdSecrets) File.Delete(secretPath);
            throw;
        }
    }

    public static AgentClientConfiguration ReadConfiguration(string path)
    {
        var json = ConfigurationFileStorage.ReadText(path);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allowed = Properties.Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new AgentClientValidationException("Configuration contains duplicate keys; remove duplicates before loading.");
                if (!allowed.Contains(property.Name)) throw new AgentClientValidationException("Configuration contains an unsupported key; check the documented schema.");
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new JsonException();
            }
            return JsonSerializer.Deserialize<AgentClientConfiguration>(json, AgentClientConfiguration.JsonOptions) ?? AgentClientConfiguration.Empty;
        }
        catch (JsonException)
        {
            throw new AgentClientValidationException("Configuration is not a valid NetRatel JSON object. Check key names and string values; credentials are never included in this diagnostic.");
        }
    }

    public static AgentClientConfiguration ResolveSecretReferences(AgentClientConfiguration configuration)
    {
        string? Resolve(string? inline, string? file, string field)
        {
            if (!string.IsNullOrWhiteSpace(inline) && !string.IsNullOrWhiteSpace(file))
                throw new AgentClientValidationException($"{field} has competing inline and file sources. Select exactly one secret source.");
            if (string.IsNullOrWhiteSpace(file)) return inline;
            if (!Path.IsPathFullyQualified(file)) throw new AgentClientValidationException($"{field} secret-file reference must be an absolute path.");
            var secret = ConfigurationFileStorage.ReadText(file, 16_384, secret: true).TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(secret)) throw new AgentClientValidationException($"{field} secret file is empty.");
            return secret;
        }
        // Resolved configurations retain no competing file reference; diagnostics use source metadata.
        return configuration with
        {
            IntegrationCredential = Resolve(configuration.IntegrationCredential, configuration.IntegrationCredentialFile, "IntegrationCredential"),
            OidcAppPassword = Resolve(configuration.OidcAppPassword, configuration.OidcAppPasswordFile, "OidcAppPassword"),
            ApiM2MClientSecret = Resolve(configuration.ApiM2MClientSecret, configuration.ApiM2MClientSecretFile, "ApiM2MClientSecret"),
            IntegrationCredentialFile = null, OidcAppPasswordFile = null, ApiM2MClientSecretFile = null
        };
    }

    public static AgentClientAuthenticationMode AuthenticationModeOf(AgentClientConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.AuthenticationMode) &&
            Enum.TryParse<AgentClientAuthenticationMode>(configuration.AuthenticationMode, true, out var explicitMode)) return explicitMode;
        if (!string.IsNullOrWhiteSpace(configuration.IntegrationCredential) || !string.IsNullOrWhiteSpace(configuration.IntegrationCredentialFile)) return AgentClientAuthenticationMode.IntegrationCredential;
        if (HasM2M(configuration)) return AgentClientAuthenticationMode.ApiM2MClientSecret;
        return AgentClientAuthenticationMode.Oidc;
    }

    public static object Redact(AgentClientConfiguration configuration)
    {
        var values = Values(configuration).ToDictionary(pair => pair.Key,
            pair => IsSecret(pair.Key) && !string.IsNullOrEmpty(pair.Value) ? "***REDACTED***" : SafeDisplay(pair.Value), StringComparer.Ordinal);
        values.TryAdd(nameof(AgentClientConfiguration.ApiBaseUrl), AgentClientConfiguration.DefaultApiBaseUrl);
        values.TryAdd(nameof(AgentClientConfiguration.AuthenticationMode), AuthenticationModeOf(configuration).ToString());
        return values;
    }

    public static string SafeDisplay(string? value) => new((value ?? string.Empty).Where(character => !char.IsControl(character)).Take(2_048).ToArray());
    public static string PurposeFor(ConfigurationSurface surface) => surface == ConfigurationSurface.HttpMcpClient ? "HttpMcp" : "Api";
    public static string ProfilePath(ConfigurationSurface surface, string profileName, Func<string, string?>? getEnvironmentVariable = null)
    {
        ValidateProfileName(profileName);
        var environment = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var configuredRoot = environment("NETRATEL_SETUP_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Path.Combine(HttpHostingStorage.Resolve(configuredRoot), "profiles", surface.ToString().ToLowerInvariant(), profileName + ".json");
        if (string.Equals(environment("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
            throw new AgentClientValidationException("No persistent client setup mount is configured. On the deployment host run: docker compose -f compose.setup.yaml run --rm --no-deps mcp-http setup --target " +
                (surface == ConfigurationSurface.StdioMcp ? "stdio" : surface == ConfigurationSurface.HttpMcpClient ? "http-client" : "cli"));
        return Path.Combine(Path.GetDirectoryName(AgentClientConfigurationStore.DefaultPath)!, "profiles", surface.ToString().ToLowerInvariant(), profileName + ".json");
    }

    public static bool Equivalent(string? left, string? right)
        => Uri.TryCreate(left, UriKind.Absolute, out var a) && Uri.TryCreate(right, UriKind.Absolute, out var b) &&
           string.Equals(a.AbsoluteUri.TrimEnd('/'), b.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);
    internal static bool IsHttps(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    internal static bool HasOidc(AgentClientConfiguration configuration) => new[] { configuration.OidcTokenUrl, configuration.OidcClientId, configuration.OidcUsername, configuration.OidcAppPassword, configuration.OidcAppPasswordFile, configuration.OidcScope }.Any(value => !string.IsNullOrWhiteSpace(value));
    internal static bool HasM2M(AgentClientConfiguration configuration) => new[] { configuration.ApiM2MTokenUrl, configuration.ApiM2MClientId, configuration.ApiM2MClientSecret, configuration.ApiM2MClientSecretFile, configuration.ApiM2MScope }.Any(value => !string.IsNullOrWhiteSpace(value));
    internal static bool HasSecrets(AgentClientConfiguration configuration) => Values(configuration).Any(pair => IsSecret(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value));
    private static void EnsureCredentialBindings(AgentClientConfiguration previous, AgentClientConfiguration configuration)
    {
        if (HasSecrets(previous) && previous.ApiBaseUrl is { Length: > 0 } &&
            !string.Equals(previous.ApiBaseUrl, configuration.ApiBaseUrl, StringComparison.Ordinal) &&
            !Equivalent(previous.ApiBaseUrl, configuration.ApiBaseUrl))
            throw new AgentClientValidationException("Changing a credential's approved target requires explicit reauthorization/rebind; the existing profile credential cannot be sent to the new URL.");
        if ((!string.IsNullOrWhiteSpace(previous.OidcAppPassword) || !string.IsNullOrWhiteSpace(previous.OidcAppPasswordFile)) &&
            !string.Equals(previous.OidcTokenUrl, configuration.OidcTokenUrl, StringComparison.Ordinal) &&
            !Equivalent(previous.OidcTokenUrl, configuration.OidcTokenUrl))
            throw new AgentClientValidationException("Changing a credential's token endpoint requires explicit reauthorization/rebind; its password cannot be sent to a new issuer.");
    }
    private static bool IsSecret(string name) => SecretNames.Contains(name) || name.EndsWith("File", StringComparison.Ordinal);
    private static Dictionary<string, string?> Values(AgentClientConfiguration configuration)
        => Properties.Where(property => property.GetValue(configuration) is not null).ToDictionary(property => property.Name, property => (string?)property.GetValue(configuration), StringComparer.Ordinal);
    private static AgentClientConfiguration FromValues(IReadOnlyDictionary<string, string?> values)
    {
        var result = AgentClientConfiguration.Empty with { };
        foreach (var property in Properties)
            if (values.TryGetValue(property.Name, out var value)) property.SetValue(result, value);
        return result;
    }
    private static void Overlay(Dictionary<string, string?> values, Dictionary<string, string?> next,
        IDictionary<string, ConfigurationSettingSource> nextSources, Dictionary<string, ConfigurationSettingSource> sources)
    {
        foreach (var secret in SecretNames)
        {
            // At a higher precedence level, a new source replaces the lower-level alternative.
            if (next.ContainsKey(secret) && !next.ContainsKey(secret + "File")) { values.Remove(secret + "File"); sources.Remove(secret + "File"); }
            if (next.ContainsKey(secret + "File") && !next.ContainsKey(secret)) { values.Remove(secret); sources.Remove(secret); }
        }
        foreach (var pair in next) { values[pair.Key] = pair.Value; sources[pair.Key] = nextSources[pair.Key]; }
    }
    private static string SnakeCase(string value)
        => value.StartsWith("ApiM2M", StringComparison.Ordinal) ? "API_M2M_" + SnakeCase(value[6..]) :
            string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character) && (!char.IsUpper(value[index - 1]) || index + 1 < value.Length && char.IsLower(value[index + 1])) ? "_" + char.ToUpperInvariant(character) : char.ToUpperInvariant(character).ToString()));
    private static void ValidateProfileName(string name)
    {
        if (name.Length is < 1 or > 64 || !name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new AgentClientValidationException("Profile names must contain 1–64 letters, digits, '-' or '_'.");
    }
    private static string ExpandPath(string path) => Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path);
}

public sealed class AgentClientConfigurationValidator(ConfigurationSurface? surface = null) : IValidateOptions<AgentClientConfiguration>
{
    public ValidateOptionsResult Validate(string? name, AgentClientConfiguration configuration)
    {
        var errors = new List<string>();
        var api = configuration.ApiBaseUrl ?? AgentClientConfiguration.DefaultApiBaseUrl;
        var legacyLocalHttp = surface is null or ConfigurationSurface.Cli or ConfigurationSurface.AgentClient or ConfigurationSurface.StdioMcp &&
            configuration.ApprovedTarget is null && configuration.Instance is null or "dev" &&
            Uri.TryCreate(api, UriKind.Absolute, out var localUri) && localUri.Scheme == "http" && localUri.IsLoopback &&
            string.IsNullOrEmpty(localUri.UserInfo) && string.IsNullOrEmpty(localUri.Query) && string.IsNullOrEmpty(localUri.Fragment);
        if (!NetRatelConfigurationService.IsHttps(api) && !legacyLocalHttp) errors.Add("ApiBaseUrl must be an absolute HTTPS URL without user information, query or fragment (legacy localhost development HTTP is supported for local CLI/stdio only).");
        var integration = !string.IsNullOrWhiteSpace(configuration.IntegrationCredential) || !string.IsNullOrWhiteSpace(configuration.IntegrationCredentialFile);
        var oidc = NetRatelConfigurationService.HasOidc(configuration);
        var m2m = NetRatelConfigurationService.HasM2M(configuration);
        if ((integration ? 1 : 0) + (oidc ? 1 : 0) + (m2m ? 1 : 0) > 1)
            errors.Add("Select exactly one authentication mode; integration credential, OIDC, and API M2M settings cannot be combined.");
        if (configuration.AuthenticationMode is { Length: > 0 } explicitMode && (!Enum.TryParse<AgentClientAuthenticationMode>(explicitMode, true, out var parsed) || !Enum.IsDefined(parsed)))
            errors.Add("AuthenticationMode must be Oidc, IntegrationCredential, or ApiM2MClientSecret.");
        var mode = NetRatelConfigurationService.AuthenticationModeOf(configuration);
        if (configuration.AuthenticationMode is { Length: > 0 } && (mode == AgentClientAuthenticationMode.Oidc && (integration || m2m) || mode == AgentClientAuthenticationMode.IntegrationCredential && (oidc || m2m) || mode == AgentClientAuthenticationMode.ApiM2MClientSecret && (oidc || integration)))
            errors.Add("AuthenticationMode conflicts with configured credentials. Remove the other mode's fields; no fallback is performed.");
        void Required(string? value, string field) { if (string.IsNullOrWhiteSpace(value)) errors.Add(field + " is required."); }
        void TokenUrl(string? value, string field) { if (!NetRatelConfigurationService.IsHttps(value)) errors.Add(field + " must be an absolute HTTPS URL without user information, query or fragment."); }
        switch (mode)
        {
            case AgentClientAuthenticationMode.IntegrationCredential:
                Required(configuration.IntegrationCredential ?? configuration.IntegrationCredentialFile, "IntegrationCredential");
                if (surface == ConfigurationSurface.HttpMcpOutbound) errors.Add("HTTP MCP outbound gateway requires its OIDC application or API M2M service identity; a human integration credential is not a gateway identity.");
                break;
            case AgentClientAuthenticationMode.ApiM2MClientSecret:
                TokenUrl(configuration.ApiM2MTokenUrl, "ApiM2MTokenUrl");
                Required(configuration.ApiM2MClientId, "ApiM2MClientId"); Required(configuration.ApiM2MClientSecret ?? configuration.ApiM2MClientSecretFile, "ApiM2MClientSecret"); Required(configuration.ApiM2MScope, "ApiM2MScope");
                if (NetRatelConfigurationService.IsHttps(api) && !NetRatelConfigurationService.Equivalent(new Uri(new Uri(api), "/connect/token").AbsoluteUri, configuration.ApiM2MTokenUrl))
                    errors.Add("ApiM2MTokenUrl must be the selected API target's /connect/token endpoint.");
                break;
            default:
                TokenUrl(configuration.OidcTokenUrl, "OidcTokenUrl"); Required(configuration.OidcClientId, "OidcClientId"); Required(configuration.OidcUsername, "OidcUsername"); Required(configuration.OidcAppPassword ?? configuration.OidcAppPasswordFile, "OidcAppPassword");
                break;
        }
        if (configuration.Instance is { Length: > 0 } instance && instance is not ("dev" or "prod")) errors.Add("Instance must be dev or prod.");
        if (configuration.DefaultTenantId is { Length: > 0 } tenant && (!int.TryParse(tenant, out var id) || id <= 0)) errors.Add("DefaultTenantId must be a positive tenant ID.");
        if (configuration.ApprovedTarget is not null && !NetRatelConfigurationService.Equivalent(configuration.ApprovedTarget, api)) errors.Add("ApiBaseUrl differs from the credential's approved target. Reauthorize/rebind explicitly.");
        if (configuration.ApprovedOrigin is not null && NetRatelConfigurationService.IsHttps(api) && !NetRatelConfigurationService.Equivalent(configuration.ApprovedOrigin, new Uri(api).GetLeftPart(UriPartial.Authority))) errors.Add("ApiBaseUrl differs from the credential's approved origin. Reauthorize/rebind explicitly.");
        if (configuration.ApprovedTokenEndpoint is not null && !NetRatelConfigurationService.Equivalent(configuration.ApprovedTokenEndpoint,
            mode == AgentClientAuthenticationMode.ApiM2MClientSecret ? configuration.ApiM2MTokenUrl : configuration.OidcTokenUrl))
            errors.Add("The token endpoint differs from the credential's approved issuer endpoint. Reauthorize/rebind explicitly.");
        if (surface is not null && configuration.CredentialPurpose is not null && configuration.CredentialPurpose != NetRatelConfigurationService.PurposeFor(surface.Value)) errors.Add("CredentialPurpose does not match this surface.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetRatel.AgentClient;

/// <summary>
/// Reads the same explicit deployment inputs as the HTTP host. Supplied settings
/// identify an existing generation; they do not establish that it is connected.
/// Secret material is private and is never included in diagnostics or settings JSON.
/// </summary>
public sealed class ExistingHttpHostSettings
{
    private const string Http = "NetRatel__Mcp__Http__";
    private const string Delegation = "NetRatel__Mcp__Delegation__";
    private string? _inlineDelegationKey;
    private string? _delegationKeyFile;
    private AgentClientConfiguration? _environmentOutboundConfiguration;

    private ExistingHttpHostSettings() { }

    public string? Instance { get; private init; }
    public string? ApiBaseUrl { get; private init; }
    public string? OutboundConfigurationPath { get; private init; }
    public string? PublicResourceUri { get; private init; }
    public bool? LocalCredentialMode { get; private init; }
    public bool? OperatorSurfaceEnabled { get; private init; }
    public int? ListeningPort { get; private init; }
    public string? OidcAuthority { get; private init; }
    public IReadOnlyList<string> RequiredGroups { get; private init; } = [];
    public IReadOnlyList<string> RequiredScopes { get; private init; } = [];
    public IReadOnlyList<string> AllowedOrigins { get; private init; } = [];
    public string? DelegationServicePrincipal { get; private init; }
    public string? DelegationKeyId { get; private init; }
    public string? DelegationIssuer { get; private init; }
    public string? DelegationAudience { get; private init; }
    public int? DelegationLifetimeSeconds { get; private init; }
    public bool HasDelegationKey => _inlineDelegationKey is not null || _delegationKeyFile is not null;
    public bool HasOutboundIdentity => OutboundConfigurationPath is not null || _environmentOutboundConfiguration is not null;

    public override string ToString() => "ExistingHttpHostSettings (existing deployment values; secret values omitted)";

    /// <summary>Inspect only: no directory, secret, identity or configuration is written.</summary>
    public static ExistingHttpHostSettings Discover(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (environment(HttpHostingEffectiveDeployment.SourceEnvironmentVariable) is { Length: > 0 } sourcePath)
        {
            var deployment = HttpHostingEffectiveDeployment.Load(sourcePath);
            var servingEnvironment = environment;
            // Keep explicit caller/launcher overrides above deployment providers;
            // adopt protected outbound credentials through the same private export
            // path as environment identities, without rewriting their source.
            environment = key => servingEnvironment(key) ?? deployment.ReadServing("mcp-http", key);
        }
        // Canonical typed settings win over compact aliases within the environment,
        // matching NetRatelMcpHttpOptions.FromConfiguration.
        string? Read(string key, params string[] aliases)
        {
            var value = environment(key) ?? environment(key.Replace("__", ":", StringComparison.Ordinal));
            foreach (var alias in aliases) value ??= environment(alias);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        bool? Boolean(string key)
        {
            var value = Read(key);
            return value is null ? null : bool.TryParse(value, out var parsed) ? parsed
                : throw new AgentClientValidationException(key + " must explicitly be true or false. Correct the deployment setting before setup; no authentication mode was changed.");
        }
        IReadOnlyList<string> Array(string name, int maximum)
        {
            var key = Http + name;
            var values = Enumerable.Range(0, maximum + 1).Select(index => Read(key + "__" + index)).ToArray();
            var last = System.Array.FindLastIndex(values, value => value is not null);
            if (last < 0) return [];
            if (last >= maximum || values.Take(last + 1).Any(value => value is null))
                throw new AgentClientValidationException(key + " must use consecutive indices starting at zero within the documented limit.");
            return values.Take(last + 1).Select(value => value!).ToArray();
        }

        var path = Read(Http + "ConfigurationPath", "NETRATEL_MCP_CONFIG");
        var dedicated = NetRatelConfigurationService.ReadEnvironment(ConfigurationSurface.HttpMcpOutbound, environment);
        AgentClientConfiguration? outbound = null;
        AgentClientConfiguration? environmentOutbound = null;
        if (path is not null)
        {
            if (!Path.IsPathFullyQualified(path))
                throw new AgentClientValidationException("The existing HTTP MCP outbound configuration must use an absolute deployment-owned path.");
            path = Path.GetFullPath(path);
            // A deployment-owned JSON source is never rewritten. Dedicated serving
            // environment overlays still participate in the shared effective rules.
            ConfigurationFileStorage.ReadText(path, secret: true);
            outbound = NetRatelConfigurationService.ReadConfiguration(path);
            if (dedicated != AgentClientConfiguration.Empty)
            {
                var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.HttpMcpOutbound,
                    ConfigurationPath: path, GetEnvironmentVariable: environment));
                var effectiveValidation = effective.Validate();
                if (!effectiveValidation.IsValid)
                    throw new AgentClientValidationException("The existing combined HTTP MCP outbound identity is incomplete or invalid. " + string.Join(" ", effectiveValidation.Errors));
                outbound = PreserveSecretReferences(effective.Configuration, outbound, dedicated);
                environmentOutbound = outbound;
                path = null; // Export the effective snapshot; retain the original source untouched.
            }
            if (string.IsNullOrWhiteSpace(outbound.ApiBaseUrl))
                throw new AgentClientValidationException("The existing HTTP MCP outbound configuration is incomplete: ApiBaseUrl is required. Setup will not substitute another target for that identity.");
            var validation = new AgentClientConfigurationValidator(ConfigurationSurface.HttpMcpOutbound)
                .Validate(null, NetRatelConfigurationService.ResolveSecretReferences(outbound));
            if (validation.Failed)
                throw new AgentClientValidationException("The existing HTTP MCP outbound configuration is incomplete. " + string.Join(" ", validation.Failures));
        }
        else
        {
            if (dedicated != AgentClientConfiguration.Empty)
            {
                if (string.IsNullOrWhiteSpace(dedicated.ApiBaseUrl))
                    throw new AgentClientValidationException("The existing HTTP MCP outbound environment identity is incomplete: ApiBaseUrl is required. Setup will not substitute another target for that identity.");
                // Only the dedicated outbound section participates. CLI, AgentClient
                // and personal MCP-client environment credentials are never adopted.
                // Preserve file references in the private snapshot; the shared engine
                // resolves them for validation without replacing their managed source.
                var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.HttpMcpOutbound,
                    ConfigurationPath: string.Empty, GetEnvironmentVariable: environment));
                var validation = effective.Validate();
                if (!validation.IsValid)
                    throw new AgentClientValidationException("The existing HTTP MCP outbound environment identity is incomplete or invalid. " + string.Join(" ", validation.Errors));
                if (dedicated.ProfileName is not null)
                    throw new AgentClientValidationException("Select the existing HTTP MCP outbound profile through its explicit deployment-owned JSON path before adoption. Setup will not copy an implicit profile identity.");
                outbound = dedicated;
                environmentOutbound = dedicated;
            }
        }
        var instance = Read(Http + "Instance", "NETRATEL_MCP_INSTANCE")?.Trim().ToLowerInvariant() ?? outbound?.Instance?.Trim().ToLowerInvariant();
        if (instance is not null && instance is not ("dev" or "prod"))
            throw new AgentClientValidationException("The existing HTTP MCP instance must be dev or prod. Correct its deployment setting before setup.");
        if (!string.IsNullOrWhiteSpace(outbound?.Instance) && !string.Equals(outbound.Instance, instance, StringComparison.OrdinalIgnoreCase))
            throw new AgentClientValidationException("The existing outbound configuration and HTTP MCP instance disagree. Setup will not rebind that identity to another instance.");
        var api = instance is null ? null : Read(Http + (instance == "dev" ? "DevApiBaseUrl" : "ProdApiBaseUrl"),
            "NETRATEL_MCP_" + instance.ToUpperInvariant() + "_API_BASE_URL");
        if (outbound?.ApiBaseUrl is { Length: > 0 } configuredApi)
        {
            if (api is not null && !NetRatelConfigurationService.Equivalent(api, configuredApi))
                throw new AgentClientValidationException("The existing outbound configuration and selected HTTP MCP API allowlist disagree. Setup will not redirect the existing service identity.");
            api = configuredApi;
        }

        var inline = Read(Delegation + "SharedKeyBase64");
        var keyFile = Read(Delegation + "SharedKeyBase64File", Delegation + "SharedKeyBase64_FILE", "NETRATEL_MCP_DELEGATION_KEY_FILE");
        if (inline is not null && keyFile is not null)
            throw new AgentClientValidationException("Existing delegation settings select both an inline key and a key file. Keep one authoritative source in the deployment; setup will not choose or rotate a key.");
        var principal = Read(Delegation + "ServicePrincipal");
        var keyId = Read(Delegation + "KeyId", "NETRATEL_MCP_DELEGATION_KEY_ID");
        var issuer = Read(Delegation + "Issuer", "NETRATEL_MCP_DELEGATION_ISSUER");
        var audience = Read(Delegation + "Audience", "NETRATEL_MCP_DELEGATION_AUDIENCE");
        var lifetimeValue = Read(Delegation + "LifetimeSeconds");
        int? lifetime = lifetimeValue is null ? null : int.TryParse(lifetimeValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 30 and <= 300 ? parsed
            : throw new AgentClientValidationException("The existing delegation LifetimeSeconds must be between 30 and 300; no key was changed.");
        var enabled = Boolean(Delegation + "Enabled");
        var hasKey = inline is not null || keyFile is not null;
        if ((hasKey || enabled == true || keyId is not null || issuer is not null || audience is not null) &&
            (!hasKey || keyId is null || issuer is null || audience is null || principal is null))
            throw new AgentClientValidationException("Existing delegation settings are incomplete. Keep the current generation and supply its canonical key source, KeyId, Issuer, Audience and ServicePrincipal in the deployment settings. Setup will not generate a replacement key.");
        if (principal is not null && outbound?.ApiM2MClientId is { Length: > 0 } clientId && principal != clientId)
            throw new AgentClientValidationException("outbound_identity_mismatch: NetRatel:Mcp:Delegation:ServicePrincipal and NetRatel:Mcp:Outbound:ApiM2MClientId identify different services; winning sources: effective serving delegation environment and existing outbound configuration/environment. Correct the coordinated deployment settings to the existing outbound identity; preserve credential and delegation key bytes, then review again.");
        if (inline is not null) ValidateKey(inline);
        if (keyFile is not null)
        {
            if (!Path.IsPathFullyQualified(keyFile))
                throw new AgentClientValidationException("The existing delegation key file must use an absolute deployment-owned path.");
            keyFile = Path.GetFullPath(keyFile);
            ValidateKey(ConfigurationFileStorage.ReadText(keyFile, 16_384, secret: true).TrimEnd('\r', '\n'));
        }

        return new ExistingHttpHostSettings
        {
            Instance = instance, ApiBaseUrl = api, OutboundConfigurationPath = path,
            PublicResourceUri = Read(Http + "PublicResourceUri"),
            LocalCredentialMode = Boolean(Http + "LocalCredentialMode"),
            OperatorSurfaceEnabled = Boolean(Http + "OperatorSurfaceEnabled"),
            ListeningPort = ResolveListeningPort(environment),
            OidcAuthority = Read(Http + "Authority"),
            RequiredGroups = Array("RequiredGroups", 32), RequiredScopes = Array("RequiredScopes", 32), AllowedOrigins = Array("AllowedOrigins", 16),
            DelegationServicePrincipal = principal, DelegationKeyId = keyId, DelegationIssuer = issuer, DelegationAudience = audience,
            DelegationLifetimeSeconds = lifetime, _inlineDelegationKey = inline, _delegationKeyFile = keyFile,
            _environmentOutboundConfiguration = environmentOutbound
        };
    }

    private static AgentClientConfiguration PreserveSecretReferences(AgentClientConfiguration effective,
        AgentClientConfiguration lower, AgentClientConfiguration higher)
    {
        // The shared engine already validated provider precedence, credential binding
        // and resolved secret values. Restore only the reference selected by those
        // same inline/file replacement rules, without persisting its resolved value.
        static string? FileSource(string? lowerFile, string? higherInline, string? higherFile)
        {
            var selected = higherFile ?? (higherInline is not null ? null : lowerFile);
            return string.IsNullOrWhiteSpace(selected) ? null : selected;
        }
        var m2m = FileSource(lower.ApiM2MClientSecretFile, higher.ApiM2MClientSecret, higher.ApiM2MClientSecretFile);
        var oidc = FileSource(lower.OidcAppPasswordFile, higher.OidcAppPassword, higher.OidcAppPasswordFile);
        var integration = FileSource(lower.IntegrationCredentialFile, higher.IntegrationCredential, higher.IntegrationCredentialFile);
        return effective with
        {
            ApiM2MClientSecret = m2m is null ? effective.ApiM2MClientSecret : null, ApiM2MClientSecretFile = m2m,
            OidcAppPassword = oidc is null ? effective.OidcAppPassword : null, OidcAppPasswordFile = oidc,
            IntegrationCredential = integration is null ? effective.IntegrationCredential : null, IntegrationCredentialFile = integration
        };
    }

    /// <summary>Keep a selected managed JSON source, or persist the same effective
    /// dedicated environment identity privately without changing its secret sources.</summary>
    public string? MaterializeOutboundConfiguration(string outputDirectory)
    {
        if (OutboundConfigurationPath is not null) return OutboundConfigurationPath;
        if (_environmentOutboundConfiguration is null) return null;
        var material = JsonSerializer.Serialize(_environmentOutboundConfiguration, AgentClientConfiguration.JsonOptions);
        if (Encoding.UTF8.GetByteCount(material) >= 65_536)
            throw new AgentClientValidationException("The existing outbound environment exceeds the protected configuration size limit. Keep its managed sources and review their size before setup; no identity was written.");
        var output = HttpHostingStorage.Resolve(outputDirectory);
        HttpHostingStorage.ValidateHostBoundary(output);
        var path = Path.Combine(output, "adopted", "outbound.json");
        ConfigurationFileStorage.EnsureSafePath(path);
        var directory = Path.GetDirectoryName(path)!;
        if (!OperatingSystem.IsWindows() && Directory.Exists(directory) &&
            (File.GetUnixFileMode(directory) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) != 0)
            throw new AgentClientValidationException("The existing adopted-identity directory is not private. Restore owner-only access before resuming; no identity was changed.");
        if (File.Exists(path))
        {
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
                throw new AgentClientValidationException("The existing adopted outbound identity is not owner-only. Restore its private permissions before resuming; no identity was changed.");
            var previous = ConfigurationFileStorage.ReadText(path, secret: true).TrimEnd('\r', '\n');
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(previous)), SHA256.HashData(Encoding.UTF8.GetBytes(material))))
                throw new AgentClientValidationException("The effective outbound environment differs from the protected setup generation. Resume with the same deployed identity or perform explicit coordinated rotation; setup will not overwrite it.");
        }
        else ConfigurationFileStorage.WriteAtomic(path, material + "\n");
        return path;
    }

    /// <summary>
    /// Reuses a referenced key, or persists an existing inline key after the caller's
    /// storage preflight. The source and any previous adopted generation are unchanged.
    /// </summary>
    public string? MaterializeDelegationKey(string outputDirectory)
    {
        if (_delegationKeyFile is not null) return _delegationKeyFile;
        if (_inlineDelegationKey is null) return null;
        var output = HttpHostingStorage.Resolve(outputDirectory);
        HttpHostingStorage.ValidateHostBoundary(output);
        var path = Path.Combine(output, "adopted", "delegation.key");
        ConfigurationFileStorage.EnsureSafePath(path);
        var directory = Path.GetDirectoryName(path)!;
        var privateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (!OperatingSystem.IsWindows() && Directory.Exists(directory) && (File.GetUnixFileMode(directory) & ~privateDirectoryMode) != 0)
            throw new AgentClientValidationException("The existing adopted-key directory is not private. Restore owner-only access before resuming; no key was changed.");
        if (File.Exists(path))
        {
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
                throw new AgentClientValidationException("The existing adopted delegation key is not owner-only. Restore its private permissions before resuming; no key was changed.");
            var existing = ConfigurationFileStorage.ReadText(path, 16_384, secret: true).TrimEnd('\r', '\n');
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(existing)), SHA256.HashData(Encoding.UTF8.GetBytes(_inlineDelegationKey))))
                throw new AgentClientValidationException("The existing inline delegation key differs from the protected setup generation. Resume with the same deployed generation or perform explicit coordinated rotation; setup will not overwrite it.");
        }
        else ConfigurationFileStorage.WriteAtomic(path, _inlineDelegationKey + "\n");
        return path;
    }

    private static void ValidateKey(string value)
    {
        try
        {
            if (value.Length > 16_384 || value.Any(char.IsControl) || Convert.FromBase64String(value).Length < 32)
                throw new FormatException();
        }
        catch (FormatException)
        {
            throw new AgentClientValidationException("The existing delegation key must be valid base64 containing at least 32 bytes. Keep its current source; no key was generated or changed.");
        }
    }

    private static int? ResolveListeningPort(Func<string, string?> environment)
    {
        var urls = environment("ASPNETCORE_URLS");
        string? value;
        if (!string.IsNullOrWhiteSpace(urls))
        {
            var match = Regex.Match(urls, @"^http://(?:\+|\*|0\.0\.0\.0|\[::\]):([0-9]{1,5})/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                throw new AgentClientValidationException("This MCP service has multiple, HTTPS or interface-specific ASPNETCORE_URLS listeners. Keep its current listener configuration and use an explicitly reviewed supported single HTTP listener; setup will not choose a port or broaden its binding.");
            value = match.Groups[1].Value;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(environment("ASPNETCORE_HTTPS_PORTS")))
                throw new AgentClientValidationException("This MCP service has HTTPS port bindings. Keep its listener configuration and review the supported HTTP backend listener before setup; no binding was changed.");
            value = environment("ASPNETCORE_HTTP_PORTS");
        }
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
            throw new AgentClientValidationException("The existing MCP listener must select one HTTP port between 1024 and 65535. Review the deployment listener before setup; no port was changed.");
        return port;
    }
}

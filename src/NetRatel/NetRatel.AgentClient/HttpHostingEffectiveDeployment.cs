using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NetRatel.Mcp.Http;

namespace NetRatel.AgentClient;

/// <summary>
/// A private, fully rendered Compose input. Compose, rather than a second YAML
/// interpreter, resolves ordered files, interpolation and service env_file values.
/// Image defaults and protected application configuration remain distinct layers.
/// No resolved configuration or credential hashes are included in diagnostics.
/// </summary>
public sealed class HttpHostingEffectiveDeployment
{
    public const string SourceEnvironmentVariable = "NETRATEL_SETUP_EFFECTIVE_COMPOSE_SOURCE";
    private const string Http = "NetRatel__Mcp__Http__";
    private readonly Dictionary<string, Dictionary<string, string?>> _services = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string?>> _protected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Type, string Source, string Target)>> _mounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _volumeNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _imageIds = new(StringComparer.Ordinal);
    private readonly List<string> _changes = [];
    public string SourcePath { get; }
    public IReadOnlyList<string> Changes => _changes;
    public IReadOnlyDictionary<string, string> ResolvedVolumeNames => _volumeNames;

    private HttpHostingEffectiveDeployment(string sourcePath) => SourcePath = sourcePath;

    public static HttpHostingEffectiveDeployment Load(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw Problem("deployment_source_unresolved", "Compose sources", "unresolved", "launcher", "Run setup-http-mcp.sh on the deployment host with its ordered Compose files.");
        var result = new HttpHostingEffectiveDeployment(Path.GetFullPath(path));
        try
        {
            using var document = JsonDocument.Parse(ConfigurationFileStorage.ReadText(path, HttpHostingComposeDocument.MaximumSourceBytes, secret: true));
            var root = document.RootElement;
            if (!root.TryGetProperty("services", out var services)) throw new JsonException();
            if (root.TryGetProperty("x-netratel-image-ids", out var imageIds))
                foreach (var entry in imageIds.EnumerateObject()) result._imageIds[entry.Name] = entry.Value.GetString() ?? throw new JsonException();
            if (root.TryGetProperty("volumes", out var declaredVolumes))
                foreach (var volume in declaredVolumes.EnumerateObject())
                    if (volume.Value.ValueKind == JsonValueKind.Object && volume.Value.TryGetProperty("name", out var physical) && physical.GetString() is { } physicalName)
                        result._volumeNames[volume.Name] = physicalName;
            foreach (var name in new[] { "web", "api", "mcp-http" })
            {
                if (!services.TryGetProperty(name, out var service)) throw new JsonException();
                var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("x-netratel-image-environment", out var images) && images.TryGetProperty(name, out var image))
                    ReadValues(image, values, sources, "selected image default");
                if (service.TryGetProperty("environment", out var environment))
                    ReadValues(environment, values, sources, "resolved Compose environment (inline entries mask matching env_file indices)", unsetRemoves: true);
                result._services[name] = values;
                result._sources[name] = sources;
                var protectedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("x-netratel-protected-configuration", out var protectedRoot) && protectedRoot.TryGetProperty(name, out var protectedSource))
                    ReadValues(protectedSource, protectedValues, new(StringComparer.OrdinalIgnoreCase), "protected application configuration");
                result._protected[name] = protectedValues;
                result._mounts[name] = [];
                // A mounted configuration file is a real provider. Do not approve an
                // environment-only approximation when the launcher cannot inspect it.
                if (service.TryGetProperty("volumes", out var volumes) && volumes.ValueKind == JsonValueKind.Array)
                    foreach (var mount in volumes.EnumerateArray())
                        if (mount.ValueKind == JsonValueKind.Object && mount.TryGetProperty("target", out var target) && target.GetString() is { } destination)
                        {
                            var type = mount.TryGetProperty("type", out var mountType) ? mountType.GetString() ?? "" : "";
                            var mountedSource = mount.TryGetProperty("source", out var mountSource) ? mountSource.GetString() ?? "" : "";
                            result._mounts[name].Add((type, mountedSource, destination));
                            if ((destination.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || destination is "/app" or "/config") &&
                                !destination.StartsWith("/run/netratel", StringComparison.Ordinal) && protectedValues.Count == 0)
                                throw Problem("protected_configuration_unresolved", name + " application configuration", "protected source present", "Compose volume", "Supply a private resolved configuration snapshot for the mounted application settings before approval.");
                        }
            }
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            throw Problem("deployment_source_unresolved", "Compose sources", "invalid private snapshot", "launcher", "Rerun setup-http-mcp.sh to render the complete deployment; keep secret-bearing Compose output private.");
        }
    }

    private static void ReadValues(JsonElement input, Dictionary<string, string?> values, Dictionary<string, string> sources, string source, bool unsetRemoves = false)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in input.EnumerateObject())
        {
            var key = Normalize(entry.Name);
            if (!seen.Add(key)) throw new JsonException();
            if (unsetRemoves && entry.Value.ValueKind == JsonValueKind.Null)
            {
                // Compose's unset entry removes the image variable. With no
                // environment-provider key the application's lower JSON provider
                // may become effective; empty strings remain explicit overrides.
                values.Remove(key);
                sources.Remove(key);
                continue;
            }
            values[key] = entry.Value.ValueKind switch
            {
                JsonValueKind.String => entry.Value.GetString(),
                JsonValueKind.Null => null,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => entry.Value.GetRawText(),
                _ => throw new JsonException()
            };
            sources[key] = source;
        }
    }

    public string? Read(string service, string key)
    {
        key = Normalize(key);
        return _services[service].TryGetValue(key, out var value) ? value : _protected[service].GetValueOrDefault(key);
    }

    /// <summary>The serving provider order, including compact HTTP aliases above protected defaults.</summary>
    public string? ReadServing(string service, string key)
    {
        key = Normalize(key);
        foreach (var name in new[] { "RequiredGroups", "RequiredScopes", "AllowedOrigins" })
        {
            var array = Http + name;
            if ((key.Equals(array, StringComparison.OrdinalIgnoreCase) || key.StartsWith(array + "__", StringComparison.OrdinalIgnoreCase)) &&
                _services[service].Keys.Any(candidate => candidate.Equals(array, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(array + "__", StringComparison.OrdinalIgnoreCase)))
                return _services[service].TryGetValue(key, out var item) ? item : "";
        }
        if (key.StartsWith("NetRatel__Mcp__Outbound__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("NETRATEL_MCP_OUTBOUND_", StringComparison.OrdinalIgnoreCase))
        {
            var property = typeof(AgentClientConfiguration).GetProperties().FirstOrDefault(property => NetRatelConfigurationService.EnvironmentNames(ConfigurationSurface.HttpMcpOutbound, property.Name)
                .Any(name => Normalize(name).Equals(key, StringComparison.OrdinalIgnoreCase)));
            if (property is not null)
            {
                var names = NetRatelConfigurationService.EnvironmentNames(ConfigurationSurface.HttpMcpOutbound, property.Name).Select(Normalize).ToArray();
                foreach (var name in names) if (_services[service].TryGetValue(name, out var selected)) return selected;
                var secret = new[] { "ApiM2MClientSecret", "OidcAppPassword", "IntegrationCredential" }.FirstOrDefault(name => property.Name == name || property.Name == name + "File");
                if (secret is not null)
                {
                    var alternative = property.Name == secret ? secret + "File" : secret;
                    if (NetRatelConfigurationService.EnvironmentNames(ConfigurationSurface.HttpMcpOutbound, alternative).Any(name => _services[service].ContainsKey(Normalize(name)))) return "";
                }
                foreach (var name in names) if (_protected[service].TryGetValue(name, out var selected)) return selected;
                return null;
            }
        }
        const string delegationKey = "NetRatel__Mcp__Delegation__SharedKeyBase64";
        var delegationFiles = new[] { delegationKey + "File", delegationKey + "_FILE", "NETRATEL_MCP_DELEGATION_KEY_FILE" };
        if (key.Equals(delegationKey, StringComparison.OrdinalIgnoreCase))
        {
            if (_services[service].TryGetValue(delegationKey, out var inline)) return inline;
            return delegationFiles.Any(name => _services[service].ContainsKey(name)) ? "" : _protected[service].GetValueOrDefault(delegationKey);
        }
        if (delegationFiles.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in delegationFiles) if (_services[service].TryGetValue(name, out var file)) return file;
            if (_services[service].ContainsKey(delegationKey)) return "";
            foreach (var name in delegationFiles) if (_protected[service].TryGetValue(name, out var file)) return file;
            return null;
        }
        var alias = key.ToUpperInvariant() switch
        {
            "NETRATEL__MCP__HTTP__INSTANCE" => "NETRATEL_MCP_INSTANCE",
            "NETRATEL__MCP__HTTP__CONFIGURATIONPATH" => "NETRATEL_MCP_CONFIG",
            "NETRATEL__MCP__HTTP__DEVAPIBASEURL" => "NETRATEL_MCP_DEV_API_BASE_URL",
            "NETRATEL__MCP__HTTP__PRODAPIBASEURL" => "NETRATEL_MCP_PROD_API_BASE_URL",
            _ => null
        };
        if (_services[service].TryGetValue(key, out var value)) return value;
        if (alias is not null && _services[service].TryGetValue(alias, out value)) return value;
        return _protected[service].GetValueOrDefault(key) ?? (alias is null ? null : _protected[service].GetValueOrDefault(alias));
    }

    private string Source(string service, string key) => _sources[service].GetValueOrDefault(Normalize(key)) ?? "protected application configuration/default";

    /// <summary>Refine explicit changes against the complete source, then validate the same options the serving host uses.</summary>
    public void Prepare(HttpHostingSetupRequest request, Dictionary<string, string> web, Dictionary<string, string> api, Dictionary<string, string> mcp)
    {
        _changes.Clear();
        foreach (var target in new[] { "/var/lib/netratel/clients", "/setup-output" })
        {
            var mounts = _mounts["mcp-http"].Where(mount => mount.Target == target).ToArray();
            if (mounts.Length > 1) throw Problem("persistent_storage_conflict", target, "duplicate storage targets", "effective Compose volumes", "Select the existing persistent storage unambiguously through the deployment owner before approval.");
            if (mounts.Length == 0) continue;
            var mount = mounts[0];
            var selected = target == "/var/lib/netratel/clients" ? mcp.GetValueOrDefault("NETRATEL_CLIENT_VOLUME") : HttpHostingStorage.VolumeName;
            var physical = _volumeNames.GetValueOrDefault(mount.Source) ?? mount.Source;
            if (mount.Type != "volume" || string.IsNullOrEmpty(selected) || physical != selected)
                throw Problem("persistent_storage_conflict", target, "existing storage differs from selected named volume", "effective Compose volumes", "Rerun the launcher with this deployment's existing persistent client/setup volumes; setup will not replace or move its state.");
        }
        var principal = Read("mcp-http", "NetRatel__Mcp__Delegation__ServicePrincipal");
        var outbound = ReadServing("mcp-http", "NetRatel__Mcp__Outbound__ApiM2MClientId");
        if (!string.IsNullOrWhiteSpace(principal) && !string.IsNullOrWhiteSpace(outbound) && !string.Equals(principal, outbound, StringComparison.Ordinal))
            throw Problem("outbound_identity_mismatch", "NetRatel:Mcp:Delegation:ServicePrincipal / NetRatel:Mcp:Outbound:ApiM2MClientId", "different identities", Source("mcp-http", "NetRatel__Mcp__Delegation__ServicePrincipal") + " / " + Source("mcp-http", "NetRatel__Mcp__Outbound__ApiM2MClientId"), "Correct those coordinated deployment identity settings to the existing outbound identity. Preserve credential and delegation key bytes, then review again.");
        if (!string.IsNullOrWhiteSpace(principal) && !string.Equals(principal, mcp["NetRatel__Mcp__Delegation__ServicePrincipal"], StringComparison.Ordinal))
            throw Problem("outbound_identity_changed", "NetRatel:Mcp:Delegation:ServicePrincipal", "existing identity would change", Source("mcp-http", "NetRatel__Mcp__Delegation__ServicePrincipal"), "Adopt the existing outbound configuration and its delegation identity; setup cannot substitute a new service principal.");
        if (request.ExistingOutboundConfigurationPath is null && new[] { "ApiM2MClientId", "ApiM2MClientSecret", "ApiM2MClientSecretFile", "OidcClientId", "OidcAppPassword", "OidcAppPasswordFile", "IntegrationCredential", "IntegrationCredentialFile" }
                .Any(name => !string.IsNullOrWhiteSpace(ReadServing("mcp-http", "NetRatel__Mcp__Outbound__" + name))))
        {
            // A resumed host may be using the generation this setup originally
            // created. Reuse it only after comparing its existing credential bytes.
            var savedSecret = Path.Combine(HttpHostingStorage.Resolve(request.OutputDirectory), "secrets", "outbound-m2m.secret");
            var currentSecret = ReadServing("mcp-http", "NetRatel__Mcp__Outbound__ApiM2MClientSecret");
            if (string.IsNullOrWhiteSpace(currentSecret) && ReadServing("mcp-http", "NetRatel__Mcp__Outbound__ApiM2MClientSecretFile") is { Length: > 0 } currentSecretPath && File.Exists(currentSecretPath))
                currentSecret = ConfigurationFileStorage.ReadText(currentSecretPath, 16_384, secret: true).TrimEnd('\r', '\n');
            var sameSavedGeneration = File.Exists(Path.Combine(HttpHostingStorage.Resolve(request.OutputDirectory), ".http-host.setup.json")) &&
                File.Exists(savedSecret) && outbound == "netratel-mcp-http" && !string.IsNullOrWhiteSpace(currentSecret) &&
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(currentSecret),
                    System.Text.Encoding.UTF8.GetBytes(ConfigurationFileStorage.ReadText(savedSecret, 16_384, secret: true).TrimEnd('\r', '\n')));
            if (!sameSavedGeneration)
                throw Problem("outbound_generation_unadopted", "NetRatel:Mcp:Outbound", "existing outbound identity was not adopted", "effective protected/environment outbound provider", "Resume setup through its canonical launcher so the existing protected outbound provider is adopted unchanged; no replacement credential will be generated.");
        }
        var selectedKeyPath = request.ExistingDelegationKeyFile ?? Path.Combine(HttpHostingStorage.Resolve(request.OutputDirectory), "secrets", "delegation.key");
        foreach (var service in new[] { "mcp-http", "api" })
        {
            var inlineKey = ReadServing(service, "NetRatel__Mcp__Delegation__SharedKeyBase64");
            var keyPath = ReadServing(service, "NetRatel__Mcp__Delegation__SharedKeyBase64File");
            if (string.IsNullOrWhiteSpace(inlineKey) && string.IsNullOrWhiteSpace(keyPath)) continue;
            if (!File.Exists(selectedKeyPath))
                throw Problem("delegation_generation_unadopted", "NetRatel:Mcp:Delegation:SharedKeyBase64/File", "existing key is not adopted", Source(service, "NetRatel__Mcp__Delegation__SharedKeyBase64File"), "Adopt the deployment's existing protected delegation key before approval; setup will not generate a replacement key.");
            var existingKey = !string.IsNullOrWhiteSpace(inlineKey) ? inlineKey.Trim() : File.Exists(keyPath) ? ConfigurationFileStorage.ReadText(keyPath!, 16_384, secret: true).Trim() : null;
            if (existingKey is null)
                throw Problem("delegation_key_equality_unresolved", "NetRatel:Mcp:Delegation:SharedKeyBase64File", "existing key equality unavailable", Source(service, "NetRatel__Mcp__Delegation__SharedKeyBase64File"), "Run setup through the existing MCP service with its protected key mounts available. Preserve the original API/MCP key generation; no key values were displayed.");
            var selectedKey = ConfigurationFileStorage.ReadText(selectedKeyPath, 16_384, secret: true).Trim();
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(existingKey), System.Text.Encoding.UTF8.GetBytes(selectedKey)))
                throw Problem("delegation_key_mismatch", "NetRatel:Mcp:Delegation:SharedKeyBase64/File", "existing and selected key bytes differ", Source(service, "NetRatel__Mcp__Delegation__SharedKeyBase64File"), "Restore the coordinated existing API/MCP key source before approval. Setup never rotates key material; values and hashes are withheld.");
        }

        var apiEnvironment = Read("api", "DOTNET_ENVIRONMENT") ?? Read("api", "ASPNETCORE_ENVIRONMENT") ?? "Production";
        var expectedInstance = apiEnvironment.Equals("Production", StringComparison.OrdinalIgnoreCase) ? "prod" : apiEnvironment.Equals("Development", StringComparison.OrdinalIgnoreCase) ? "dev" : null;
        if (expectedInstance != request.Instance)
            throw Problem("api_instance_mismatch", "DOTNET_ENVIRONMENT / ASPNETCORE_ENVIRONMENT / NetRatel:Mcp:Http:Instance", "API " + SafeEnvironment(apiEnvironment) + "; selected " + request.Instance, Source("api", "DOTNET_ENVIRONMENT") + " / " + Source("api", "ASPNETCORE_ENVIRONMENT"), "Select " + (expectedInstance ?? "an instance supported by the API's existing environment") + " in setup. The API environment is deployment-owned and will not be rewritten.");

        // Preserve occupied indices. Append only the missing service caller; a
        // generated replacement array could shadow unrelated env_file callers.
        var existingCallers = EffectiveValues("api").Where(pair => pair.Key.StartsWith("M2M__AllowedCallerClientIds__", StringComparison.OrdinalIgnoreCase))
            .Select(pair => (pair.Key, pair.Value, Index: ParseIndex(pair.Key))).OrderBy(pair => pair.Index).ToArray();
        var allCallers = existingCallers.Select(pair => pair.Value).OfType<string>().Concat(request.ExistingM2MCallerClientIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList();
        if (allCallers.Any(value => value == "*"))
            throw Problem("caller_allowlist_invalid", "M2M:AllowedCallerClientIds", "wildcard caller present", "effective API configuration", "Replace the wildcard with explicit existing integration identities through the deployment owner; setup never grants wildcard callers.");
        if (existingCallers.Any(pair => pair.Index < 0 || string.IsNullOrWhiteSpace(pair.Value)) || existingCallers.Select(pair => pair.Index).Distinct().Count() != existingCallers.Length ||
            existingCallers.Select(pair => pair.Value).Distinct(StringComparer.Ordinal).Count() != existingCallers.Length)
            throw Problem("caller_allowlist_invalid", "M2M:AllowedCallerClientIds", "invalid indexed entries", "effective API configuration", "Use explicit numeric indices with nonempty caller identities before approval.");
        foreach (var key in api.Keys.Where(key => key.StartsWith("M2M__AllowedCallerClientIds__", StringComparison.OrdinalIgnoreCase)).ToArray()) api.Remove(key);
        var next = existingCallers.Length == 0 ? 0 : existingCallers.Max(pair => pair.Index) + 1;
        var effectivePrincipal = mcp["NetRatel__Mcp__Delegation__ServicePrincipal"];
        if (!allCallers.Contains(effectivePrincipal, StringComparer.Ordinal)) allCallers.Add(effectivePrincipal);
        if (next + allCallers.Count(caller => !existingCallers.Any(pair => pair.Value == caller)) > 128 ||
            allCallers.Any(value => value.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(value[0]) || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_')))
            throw Problem("caller_allowlist_invalid", "M2M:AllowedCallerClientIds", "unsupported caller identity or no available index", "effective API configuration", "Keep explicit bounded caller identities and an available index below 128; review the existing integration list with its owner.");
        foreach (var caller in allCallers.Where(caller => !existingCallers.Any(pair => pair.Value == caller)))
            api["M2M__AllowedCallerClientIds__" + next++] = caller;
        if (existingCallers.Length > 0)
            _changes.Add("M2M:AllowedCallerClientIds retains each occupied effective index; resolved Compose inline entries mask matching env_file indices. Add only missing explicit callers at the next available index.");

        // Both the canonical key and compact alias are explicitly cleared. An
        // empty canonical value outranks an alias/image default in the actual binder.
        var inactive = request.Instance == "prod" ? "DevApiBaseUrl" : "ProdApiBaseUrl";
        var inactiveAlias = request.Instance == "prod" ? "NETRATEL_MCP_DEV_API_BASE_URL" : "NETRATEL_MCP_PROD_API_BASE_URL";
        var inheritedInactive = _services["mcp-http"].TryGetValue(Http + inactive, out var canonicalInactive) ? canonicalInactive :
            _services["mcp-http"].TryGetValue(inactiveAlias, out var aliasInactive) ? aliasInactive :
            _protected["mcp-http"].GetValueOrDefault(Http + inactive) ?? _protected["mcp-http"].GetValueOrDefault(inactiveAlias);
        if (!string.IsNullOrWhiteSpace(inheritedInactive) && NetRatelConfigurationService.Equivalent(inheritedInactive, request.ApiBaseUrl))
        {
            mcp[Http + inactive] = "";
            mcp[inactiveAlias] = "";
            _changes.Add("Clear NetRatel:Mcp:Http:" + inactive + " and " + inactiveAlias + " at the winning Compose environment source (review required); its equivalent selected endpoint is retained. Explicit empty values suppress lower env_file/image defaults.");
        }
        ValidateProposed(request, web, api, mcp);
        foreach (var (service, changes) in new[] { ("web", web), ("api", api), ("mcp-http", mcp) })
            foreach (var (key, value) in changes)
                if (!string.Equals(Read(service, key), value, StringComparison.Ordinal))
                    _changes.Add(service + ": " + Canonical(key) + " = " + Display(key, value) + " (reviewed Compose environment override; previous source: " + Source(service, key) + ").");
    }

    private Dictionary<string, string?> EffectiveValues(string service)
    {
        var values = new Dictionary<string, string?>(_protected[service], StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _services[service]) values[pair.Key] = pair.Value;
        return values;
    }

    public void ValidateProposed(HttpHostingSetupRequest request, IReadOnlyDictionary<string, string> web, IReadOnlyDictionary<string, string> api, IReadOnlyDictionary<string, string> mcp)
    {
        Dictionary<string, string?> Proposed(string service, IReadOnlyDictionary<string, string> changes)
        {
            var values = EffectiveValues(service);
            foreach (var pair in changes) values[Normalize(pair.Key)] = pair.Value;
            return values;
        }
        var mcpValues = Proposed("mcp-http", mcp);
        var servingEnvironment = new Dictionary<string, string?>(_services["mcp-http"], StringComparer.OrdinalIgnoreCase);
        foreach (var pair in mcp) servingEnvironment[Normalize(pair.Key)] = pair.Value;
        ValidateServingOptions(servingEnvironment, _protected["mcp-http"]);
        var apiValues = Proposed("api", api);
        var mode = apiValues.GetValueOrDefault("Integration__HttpMcp__AuthenticationMode");
        var local = mcpValues.GetValueOrDefault(Http + "LocalCredentialMode");
        if (!string.Equals(mode, local == "true" ? "LocalCredential" : "Oidc", StringComparison.OrdinalIgnoreCase))
            throw Problem("http_authentication_mode_mismatch", "Integration:HttpMcp:AuthenticationMode / NetRatel:Mcp:Http:LocalCredentialMode", "API/MCP modes disagree", "complete proposed deployment", "Review one explicit supported authentication mode for API and MCP together.");
        if (!string.Equals(apiValues.GetValueOrDefault("NetRatel__Mcp__Delegation__ServicePrincipal"), mcpValues.GetValueOrDefault("NetRatel__Mcp__Delegation__ServicePrincipal"), StringComparison.Ordinal))
            throw Problem("delegation_identity_mismatch", "NetRatel:Mcp:Delegation:ServicePrincipal", "API/MCP identities differ", "complete proposed deployment", "Keep the existing coordinated service principal in both API and MCP configuration.");
    }

    public static void ValidateServingOptions(IReadOnlyDictionary<string, string?> environment, IReadOnlyDictionary<string, string?>? protectedConfiguration = null)
    {
        try
        {
            var options = BindServingOptions(environment, protectedConfiguration);
            var result = new NetRatelMcpHttpOptionsValidator().Validate(null, options);
            if (result.Failed)
                throw Problem("serving_options_invalid", "NetRatel:Mcp:Http", "serving options rejected", "complete proposed deployment", string.Join(" ", result.Failures) + " Correct the winning deployment source and review again.");
        }
        catch (InvalidOperationException)
        {
            throw Problem("serving_options_invalid", "NetRatel:Mcp:Http", "invalid typed value", "complete proposed deployment", "Correct the typed HTTP MCP setting in its winning deployment source and review again.");
        }
    }

    private static NetRatelMcpHttpOptions BindServingOptions(IReadOnlyDictionary<string, string?> environment, IReadOnlyDictionary<string, string?>? protectedConfiguration = null)
    {
        var builder = new ConfigurationBuilder();
        if (protectedConfiguration is not null)
            builder.AddInMemoryCollection(protectedConfiguration.Select(pair => new KeyValuePair<string, string?>(Canonical(pair.Key), pair.Value)));
        return NetRatelMcpHttpOptions.FromConfiguration(builder.AddInMemoryCollection(environment.Select(pair => new KeyValuePair<string, string?>(Canonical(pair.Key), pair.Value))).Build());
    }

    private static Dictionary<string, object?> OptionsProjection(NetRatelMcpHttpOptions options) => typeof(NetRatelMcpHttpOptions).GetProperties()
        .Where(property => property.CanWrite).ToDictionary(property => property.Name, property => property.GetValue(options), StringComparer.Ordinal);

    /// <summary>Compare a recreated MCP process with the saved review, including its actual private providers.</summary>
    public static void ValidateRuntimeProposal(string outputDirectory, Func<string, string?>? environment = null, string? runtimeSourcePath = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        ValidateRuntimeProposalCore(outputDirectory, (_, key) => environment(key), runtimeSourcePath, mcpOnly: true);
    }

    /// <summary>Compare each recreated service with the saved review using the launcher's private serving snapshot.</summary>
    public static void ValidateRuntimeProposal(string outputDirectory, Func<string, string, string?> environment, string? runtimeSourcePath = null)
        => ValidateRuntimeProposalCore(outputDirectory, environment, runtimeSourcePath, mcpOnly: false);

    private static void ValidateRuntimeProposalCore(string outputDirectory, Func<string, string, string?> environment, string? runtimeSourcePath, bool mcpOnly)
    {
        var path = Path.Combine(HttpHostingStorage.Resolve(outputDirectory), ".effective-proposal.json");
        if (!File.Exists(path))
            throw Problem("effective_proposal_missing", "effective deployment proposal", "no reviewed proposal", "saved setup", "Review and export the complete deployment before verification.");
        try
        {
            using var document = JsonDocument.Parse(ConfigurationFileStorage.ReadText(path, HttpHostingComposeDocument.MaximumSourceBytes, secret: true));
            var proposal = document.RootElement;
            runtimeSourcePath ??= Environment.GetEnvironmentVariable("NETRATEL_SETUP_RUNTIME_ENVIRONMENT_SOURCE");
            using var runtime = string.IsNullOrEmpty(runtimeSourcePath) ? null : JsonDocument.Parse(ConfigurationFileStorage.ReadText(runtimeSourcePath, HttpHostingComposeDocument.MaximumSourceBytes, secret: true));
            var actualEnvironment = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
            var actualProtected = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
            foreach (var service in new[] { "web", "api", "mcp-http" })
            {
                actualEnvironment[service] = new(StringComparer.OrdinalIgnoreCase);
                actualProtected[service] = new(StringComparer.OrdinalIgnoreCase);
                if (runtime is not null)
                {
                    if (!runtime.RootElement.TryGetProperty(service, out var serviceEnvironment)) throw new JsonException();
                    ReadValues(serviceEnvironment, actualEnvironment[service], new(StringComparer.OrdinalIgnoreCase), "serving container environment");
                    if (!runtime.RootElement.TryGetProperty("x-netratel-protected-configuration", out var providers) || !providers.TryGetProperty(service, out var provider))
                        throw Problem("runtime_provider_unresolved", service + " protected configuration", "serving provider snapshot unavailable", "serving container", "Rerun the canonical launcher with --verify so it observes actual serving files before consent.");
                    ReadValues(provider, actualProtected[service], new(StringComparer.OrdinalIgnoreCase), "serving protected configuration");
                }
            }
            if (runtime is null && (proposal.GetProperty("RequiresProtectedRuntimeSnapshot").GetBoolean() || proposal.GetProperty("ExpectedImages").EnumerateObject().Any()))
                throw Problem("runtime_provider_unresolved", "protected configuration / images", "serving provider snapshot unavailable", "serving containers", "Rerun setup-http-mcp.sh with --verify to inspect the actual recreated service providers before consent.");
            foreach (var image in proposal.GetProperty("ExpectedImages").EnumerateObject())
                if (runtime is null || !runtime.RootElement.TryGetProperty("x-netratel-runtime-images", out var images) || !images.TryGetProperty(image.Name, out var actualImage) || actualImage.GetString() != image.Value.GetString())
                    throw Problem("effective_runtime_mismatch", image.Name + " image identity", "serving image differs from review", "serving container image", "Recreate all three services using the reviewed image identities, then resume Verify.");

            string? ReadRuntime(string service, string key)
            {
                key = Normalize(key);
                if (actualEnvironment[service].TryGetValue(key, out var value)) return value;
                // With a complete runtime snapshot, absent environment keys must
                // fall through to actual serving files, never the setup process.
                if (runtime is null && (environment(service, key) ?? environment(service, Canonical(key))) is { } supplied) return supplied;
                return actualProtected[service].GetValueOrDefault(key);
            }
            foreach (var service in mcpOnly ? new[] { "mcp-http" } : new[] { "web", "api", "mcp-http" })
                foreach (var entry in proposal.GetProperty("ExpectedConfiguration").GetProperty(service).EnumerateObject())
                {
                    if (entry.Name.StartsWith("NETRATEL_SETUP_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(ReadRuntime(service, entry.Name) ?? "", entry.Value.GetString() ?? "", StringComparison.Ordinal))
                        throw Problem("effective_runtime_mismatch", service + " " + Canonical(entry.Name), "saved proposal and serving provider differ", "actual serving configuration", "Apply the reviewed complete deployment through its existing manager, recreate API/Web/MCP, then resume Verify.");
                }
            if (!mcpOnly)
            {
                var apiEnvironment = ReadRuntime("api", "DOTNET_ENVIRONMENT") ?? ReadRuntime("api", "ASPNETCORE_ENVIRONMENT") ?? "Production";
                if (!string.Equals(apiEnvironment, proposal.GetProperty("ApiEnvironment").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw Problem("effective_runtime_mismatch", "API DOTNET_ENVIRONMENT / ASPNETCORE_ENVIRONMENT", "API environment differs from review", "actual serving API environment", "Keep the reviewed API environment and selected instance coordinated, recreate the deployment, then Verify.");
            }
            var current = actualEnvironment["mcp-http"];
            if (runtime is null)
            {
                foreach (var entry in proposal.GetProperty("ExpectedEnvironment").GetProperty("mcp-http").EnumerateObject())
                    if ((environment("mcp-http", entry.Name) ?? environment("mcp-http", Canonical(entry.Name))) is { } value) current[Normalize(entry.Name)] = value;
                foreach (var property in typeof(NetRatelMcpHttpOptions).GetProperties().Where(property => property.CanWrite))
                {
                    var key = Http + property.Name;
                    if ((environment("mcp-http", key) ?? environment("mcp-http", Canonical(key))) is { } value) current[key] = value;
                }
            }
            ValidateServingOptions(current, actualProtected["mcp-http"]);
            var actualOptions = JsonSerializer.Serialize(OptionsProjection(BindServingOptions(current, actualProtected["mcp-http"])));
            if (actualOptions != JsonSerializer.Serialize(proposal.GetProperty("ServingOptions")))
                throw Problem("effective_runtime_mismatch", "NetRatel:Mcp:Http serving options", "actual bound options differ from review", "actual serving environment and protected providers", "Recreate MCP with the reviewed provider files and image, then resume Verify.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw Problem("effective_proposal_invalid", "effective deployment proposal/runtime snapshot", "private metadata invalid", "saved setup or serving snapshot", "Reopen setup and review the complete deployment again; preserve its existing identity generation.");
        }
    }

    public string RedactedProposal(HttpHostingSetupRequest request, IReadOnlyDictionary<string, string> web, IReadOnlyDictionary<string, string> api, IReadOnlyDictionary<string, string> mcp)
    {
        var changes = new[] { (Name: "web", Values: web), (Name: "api", Values: api), (Name: "mcp-http", Values: mcp) };
        var expected = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var service in changes)
        {
            var values = EffectiveValues(service.Name);
            foreach (var pair in service.Values) values[Normalize(pair.Key)] = pair.Value;
            expected[service.Name] = values.Where(pair => RelevantSetting(pair.Key) && !Sensitive(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
        var serving = new Dictionary<string, string?>(_services["mcp-http"], StringComparer.OrdinalIgnoreCase);
        foreach (var pair in mcp) serving[Normalize(pair.Key)] = pair.Value;
        return JsonSerializer.Serialize(new { Instance = request.Instance, request.LocalCredentialMode, PublicResourceUri = request.PublicResourceUri ?? request.PublicOrigin.TrimEnd('/') + "/mcp", request.ApiBaseUrl,
            ApiEnvironment = Read("api", "DOTNET_ENVIRONMENT") ?? Read("api", "ASPNETCORE_ENVIRONMENT") ?? "Production",
            ExpectedEnvironment = changes.ToDictionary(service => service.Name, service => service.Values.Where(pair => !Sensitive(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value)),
            ExpectedConfiguration = expected, ExpectedImages = _imageIds, RequiresProtectedRuntimeSnapshot = _protected.Any(service => service.Value.Count > 0),
            ServingOptions = OptionsProjection(BindServingOptions(serving, _protected["mcp-http"])), Changes = _changes }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static bool RelevantSetting(string key) => new[] { "NetRatel__Mcp__", "Integration__HttpMcp__", "M2M__AllowedCallerClientIds__", "WebGateway__", "NETRATEL_MCP_", "NETRATEL_CLIENT_" }
        .Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) || key.Equals("ApiBaseUrl", StringComparison.OrdinalIgnoreCase) || key.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase) || key.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase) || key.Equals("ASPNETCORE_URLS", StringComparison.OrdinalIgnoreCase);

    private static int ParseIndex(string key) => int.TryParse(key[(key.LastIndexOf("__", StringComparison.Ordinal) + 2)..], out var index) && index is >= 0 and < 128 ? index : -1;
    private static string Normalize(string key) => key.Replace(":", "__", StringComparison.Ordinal);
    private static string Canonical(string key) => key.Replace("__", ":", StringComparison.Ordinal);
    private static bool Sensitive(string key) => key.Contains("Secret", StringComparison.OrdinalIgnoreCase) || key.Contains("Password", StringComparison.OrdinalIgnoreCase) || key.Contains("SharedKey", StringComparison.OrdinalIgnoreCase) || key.Contains("Credential", StringComparison.OrdinalIgnoreCase) && !key.EndsWith("LocalCredentialMode", StringComparison.OrdinalIgnoreCase);
    private static string Display(string key, string value) => Sensitive(key) ? "[protected value/reference retained]" : value.Length == 0 ? "[explicit empty]" : value;
    private static string SafeEnvironment(string value) => value.Equals("Production", StringComparison.OrdinalIgnoreCase) ? "Production" : value.Equals("Development", StringComparison.OrdinalIgnoreCase) ? "Development" : "unsupported environment";
    private static AgentClientValidationException Problem(string code, string settings, string effective, string source, string action)
        => new(code + ": " + settings + "; effective: " + effective + "; winning source: " + source + ". " + action);
}

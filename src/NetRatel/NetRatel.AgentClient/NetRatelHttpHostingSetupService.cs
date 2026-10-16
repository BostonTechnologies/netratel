using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetRatel.AgentClient;

public sealed record HttpHostingSetupRequest(
    string OutputDirectory,
    string PublicOrigin,
    string ApiBaseUrl,
    string Instance = "prod",
    bool LocalCredentialMode = true,
    string WebApiBackendUrl = "http://api:9222/",
    string WebMcpBackendUrl = "http://mcp-http:9224/",
    string? OidcAuthority = null,
    IReadOnlyList<string>? RequiredGroups = null,
    IReadOnlyList<string>? RequiredScopes = null,
    string? ExistingOutboundConfigurationPath = null,
    string? PublicResourceUri = null,
    IReadOnlyList<string>? AllowedOrigins = null,
    bool? OperatorSurfaceEnabled = null,
    int ListeningPort = 9224,
    IReadOnlyList<string>? ExistingM2MCallerClientIds = null,
    string? ComposeOutputDirectory = null,
    string? DelegationServicePrincipal = null,
    string? ExistingDelegationKeyFile = null,
    string? DelegationKeyId = null,
    string? DelegationIssuer = null,
    string? DelegationAudience = null,
    string? ApprovedRegistrationFingerprint = null,
    string ApiServiceAudience = "netratel.api",
    string? ApiServiceAuthority = null,
    string DeploymentManager = "release-compose",
    string? ComposeSourcePath = null,
    int DelegationLifetimeSeconds = 90,
    string? ExistingWebApiBackendUrl = null,
    string? ExistingWebMcpBackendUrl = null,
    bool ExistingWebEnvironmentObserved = false);

public sealed record HttpHostingSetupPreview(
    IReadOnlyList<string> Changes, IReadOnlyList<string> Files,
    IReadOnlyList<string> Instructions, bool ExistingConfiguration);
public sealed record HttpHostingSetupResult(IReadOnlyList<string> Files, IReadOnlyList<string> Instructions);

/// <summary>
/// Prepares a deployment bundle without starting an HTTP server or changing running services.
/// Secrets have independent files and are generated once; ordinary setup never rotates them.
/// </summary>
public sealed class NetRatelHttpHostingSetupService
{
    private const string ServicePrincipal = "netratel-mcp-http";
    // POSIX entrypoint shells discard environment names containing hyphens.
    // The owning API resolver maps this shell-safe registration alias back to
    // the unchanged canonical caller identity through its existing alias boundary.
    private const string RegistrationEnvironmentAlias = "netratel_mcp_http";
    private const string SecretMount = "/run/netratel-mcp-secrets";
    private string? _reviewedRequestSignature;
    private Dictionary<string, string?>? _reviewedHashes;
    public string? PreparedServicePrincipal { get; private set; }

    /// <summary>Prepare only the independent protected identity generation for browser approval.
    /// Existing identities are reused and no effective configuration is replaced.</summary>
    public string PrepareRegistration(HttpHostingSetupRequest request)
    {
        var bundle = Prepare(request);
        PreparedServicePrincipal = bundle.ServicePrincipal;
        HttpHostingStorage.ValidateWritable(HttpHostingStorage.Resolve(request.OutputDirectory));
        HttpHostingStorage.ValidateHostBoundary(HttpHostingStorage.Resolve(request.OutputDirectory));
        EnsureGeneration(bundle);
        return RegistrationFingerprint(bundle);
    }

    /// <summary>Restore reviewed host settings without silently rebinding a saved identity.</summary>
    public static HttpHostingSetupRequest? LoadRequest(string? outputDirectory = null, string? publicOrigin = null)
    {
        var output = HttpHostingStorage.Resolve(outputDirectory);
        var path = Path.Combine(output, ".http-host.setup.json");
        if (!File.Exists(path)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<HttpHostingSetupRequest>(ConfigurationFileStorage.ReadText(path, secret: true))
                ?? throw new JsonException();
            if (string.IsNullOrWhiteSpace(saved.OutputDirectory) || string.IsNullOrWhiteSpace(saved.PublicOrigin) ||
                string.IsNullOrWhiteSpace(saved.WebApiBackendUrl) || string.IsNullOrWhiteSpace(saved.WebMcpBackendUrl)) throw new JsonException();
            if (!string.Equals(Path.GetFullPath(saved.OutputDirectory), output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new AgentClientValidationException("The saved hosting setup belongs to another storage location. Select its existing location or export a separately reviewed deployment.");
            if (publicOrigin is not null && !NetRatelConfigurationService.Equivalent(InstanceSetupAddress.Parse(saved.PublicOrigin).Origin, InstanceSetupAddress.Parse(publicOrigin).Origin))
                throw new AgentClientValidationException("This setup location belongs to another instance. Choose a separate output directory in Advanced; existing host identities are never rebound.");
            var restored = saved with { OutputDirectory = output, ApprovedRegistrationFingerprint = null };
            Prepare(restored); // Retain strict internal validation for persisted advanced inputs.
            return restored;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new AgentClientValidationException("Saved host setup settings are invalid. Keep the existing identity generation; select an Advanced export directory or restore the protected settings file.");
        }
    }

    public static string ApplyCommand(HttpHostingSetupRequest request)
    {
        var output = HttpHostingStorage.Resolve(request.OutputDirectory);
        if (request.DeploymentManager == "komodo")
            return "In Komodo, replace this stack's Compose editor with the complete private compose.komodo.yaml exported by setup-http-mcp.sh, then Save and Deploy. Resume the same setup command and select Verify. Do not add a temporary CLI overlay.";
        var files = Environment.GetEnvironmentVariable("NETRATEL_SETUP_COMPOSE_FILES") ??
            (HttpHostingStorage.UsesValidatedSetupVolume(output) ? "compose.images.yaml,compose.mcp-http.yaml" : "compose.yaml,compose.mcp-http.yaml");
        var compose = string.Join(" ", files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(file => "-f " + ShellQuote(file)));
        var overlay = HttpHostingStorage.UsesValidatedSetupVolume(output) ? ".netratel.http-host.yaml" : Path.Combine(request.ComposeOutputDirectory ?? output, "compose.http-host.yaml");
        var extract = HttpHostingStorage.UsesValidatedSetupVolume(output)
            ? "docker compose -f compose.setup.yaml run --rm --no-deps --entrypoint cat mcp-http /setup-output/compose.http-host.yaml > " + ShellQuote(overlay) + " && " : "";
        return extract + "docker compose " + compose + " -f " + ShellQuote(overlay) + " up -d web api mcp-http";
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public HttpHostingSetupPreview Preview(HttpHostingSetupRequest request)
    {
        var bundle = Prepare(request);
        var changes = bundle.SelectedSettings.Where(change => change.StartsWith("Replace API service registration", StringComparison.Ordinal)).ToList();
        foreach (var path in bundle.ReviewedInputPaths)
            changes.Add($"Use current deployment document {path} (SHA256 {ConfigurationFileStorage.Hash(path)}; source stays unchanged; import the complete generated document through its deployment manager).");
        changes.AddRange(bundle.Configuration.Select(file =>
            !File.Exists(file.Key) ? $"Create {file.Key} (deployment setup)" :
            ConfigurationFileStorage.ReadText(file.Key) == file.Value ? $"Reuse {file.Key} (deployment setup)" :
            $"Replace {file.Key} (review required; existing file or externally managed values)"));
        changes.AddRange(bundle.SelectedSettings.Where(change => !change.StartsWith("Replace API service registration", StringComparison.Ordinal)));
        changes.Add(request.DeploymentManager == "komodo"
            ? "Import the complete compose.komodo.yaml into this stack's Komodo editor. Existing ingress, database, ports, env_file entries and unrelated settings are retained; only reviewed HTTP MCP settings and persistent mounts change."
            : "The final overlay supplies reviewed Web/API settings and replaces the managed MCP environment and env-file list; retain additional MCP runtime options in a later explicit overlay.");
        foreach (var path in bundle.SecretPaths)
            changes.Add(File.Exists(path) ? $"Reuse protected secret reference {path}" : $"Create protected secret reference {path} (value withheld)");
        foreach (var path in bundle.InputPaths)
            changes.Add($"Reuse existing reviewed input {path} (source unchanged; protected runtime export; values withheld)");
        foreach (var path in bundle.RuntimeSecrets.Keys)
            changes.Add($"{(File.Exists(path) ? "Reuse" : "Create")} protected runtime export {path} (API/MCP read-only; values withheld)");
        _reviewedRequestSignature = RequestSignature(request);
        _reviewedHashes = bundle.Configuration.Keys.Concat(bundle.SecretPaths).Concat(bundle.InputPaths).Concat(bundle.ReviewedInputPaths).Concat(bundle.RuntimeSecrets.Keys).Distinct(StringComparer.Ordinal)
            .ToDictionary(path => path, path => File.Exists(path) ? ConfigurationFileStorage.Hash(path) : null, StringComparer.Ordinal);
        return new(changes, bundle.Configuration.Keys.Concat(bundle.SecretPaths).Concat(bundle.RuntimeSecrets.Keys).ToArray(), bundle.Instructions,
            bundle.RequiresRegistrationReplacement || bundle.Configuration.Keys.Any(File.Exists));
    }

    public HttpHostingSetupResult Apply(HttpHostingSetupRequest request, bool replace = false)
    {
        if (_reviewedHashes is not null)
        {
            if (_reviewedRequestSignature != RequestSignature(request))
                throw new AgentClientValidationException("Hosting inputs changed since review. Preview the updated changes before applying.");
            foreach (var (path, reviewedHash) in _reviewedHashes)
                if ((File.Exists(path) ? ConfigurationFileStorage.Hash(path) : null) != reviewedHash)
                    throw new AgentClientValidationException("Hosting configuration or secrets changed since review. Preview the bundle again before applying.");
        }
        var bundle = Prepare(request);
        if (bundle.RequiresRegistrationReplacement && !replace)
            throw new AgentClientValidationException("The existing API caller may already have a service registration with different credentials. Review and explicitly confirm replacing only this registration with the selected existing outbound credential; setup cannot compare the API's current secret here.");
        HttpHostingStorage.ValidateWritable(HttpHostingStorage.Resolve(request.OutputDirectory));
        HttpHostingStorage.ValidateHostBoundary(HttpHostingStorage.Resolve(request.OutputDirectory));
        if (request.ApprovedRegistrationFingerprint is not null &&
            (bundle.SecretPaths.Concat(bundle.InputPaths).Any(path => !File.Exists(path)) ||
             !string.Equals(request.ApprovedRegistrationFingerprint, RegistrationFingerprint(bundle), StringComparison.Ordinal)))
            throw new AgentClientValidationException("The host identity generation changed after administrator approval. Request approval again before applying.");
        // Review every conflict before creating even the first secret. Repeated setup with
        // the same inputs is a no-op; an existing different/unknown file needs approval.
        var writes = bundle.Configuration.Where(file => !File.Exists(file.Key) || ConfigurationFileStorage.ReadText(file.Key) != file.Value)
            .Select(file => (file.Key, file.Value, Before: File.Exists(file.Key) ? ConfigurationFileStorage.Hash(file.Key) : null)).ToArray();
        if (!replace && writes.Any(file => file.Before is not null))
            throw new AgentClientValidationException("The deployment bundle has existing changes. Review Preview and explicitly confirm replacement before applying.");
        foreach (var file in writes)
        {
            ConfigurationFileStorage.EnsureSafePath(file.Key);
            if (file.Before is not null && (!ConfigurationFileStorage.IsWritable(file.Key) ||
                !OperatingSystem.IsWindows() && (File.GetUnixFileMode(file.Key) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0))
                throw new AgentClientValidationException("An existing deployment output is read-only or externally managed. Export to a separate explicitly writable directory and apply it through the deployment owner.");
        }
        // Persist each independent generation before configuration references it. Interrupted
        // setup reuses these files and cannot silently replace a working generation.
        EnsureGeneration(bundle);
        if (!OperatingSystem.IsWindows())
        {
            var output = HttpHostingStorage.Resolve(request.OutputDirectory);
            if (HttpHostingStorage.UsesValidatedSetupVolume(output))
                File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
        foreach (var (export, source) in bundle.RuntimeSecrets)
        {
            var material = bundle.RuntimeContent.GetValueOrDefault(export) ?? ConfigurationFileStorage.ReadText(source, secret: true);
            if (File.Exists(export) && ConfigurationFileStorage.ReadText(export, secret: true) != material)
                throw new AgentClientValidationException("A runtime export does not match the selected identity generation. Explicit rotation must coordinate API and MCP; setup will not overwrite it.");
            if (!File.Exists(export)) ConfigurationFileStorage.WriteAtomic(export, material);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(export, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                File.SetUnixFileMode(Path.GetDirectoryName(export)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }
        }
        foreach (var file in writes)
            ConfigurationFileStorage.WriteAtomic(file.Key, file.Value, replace, file.Before ?? string.Empty);
        _reviewedHashes = null;
        _reviewedRequestSignature = null;
        return new(bundle.Configuration.Keys.Concat(bundle.SecretPaths).Concat(bundle.RuntimeSecrets.Keys).ToArray(), bundle.Instructions);
    }

    private static void EnsureGeneration(Bundle bundle)
    {
        foreach (var path in bundle.SecretPaths)
            if (!File.Exists(path)) ConfigurationFileStorage.WriteAtomic(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");
    }

    private static string RegistrationFingerprint(Bundle bundle)
    {
        var settings = new
        {
            EffectiveConfiguration = bundle.Configuration.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            Inputs = bundle.SecretPaths.Concat(bundle.InputPaths).Order(StringComparer.Ordinal)
                .Select(path => new { Path = path, Hash = ConfigurationFileStorage.Hash(path) }).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings))));
    }

    private static Bundle Prepare(HttpHostingSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var output = HttpHostingStorage.Resolve(request.OutputDirectory);
        ConfigurationFileStorage.EnsureSafePath(output);
        HttpHostingStorage.ValidateHostBoundary(output);
        if (request.DeploymentManager is not ("release-compose" or "komodo"))
            throw new AgentClientValidationException("Choose release-compose for the supplied release files, or komodo for a stack stored in Komodo's Compose editor.");
        if (request.DeploymentManager == "komodo")
        {
            if (!HttpHostingStorage.UsesValidatedSetupVolume(output))
                throw new AgentClientValidationException("Komodo setup needs the persistent tooling volume. " + HttpHostingStorage.SetupRecoveryInstructions("server"));
            if (string.IsNullOrWhiteSpace(request.ComposeSourcePath) || !Path.IsPathFullyQualified(request.ComposeSourcePath))
                throw new AgentClientValidationException("Komodo setup needs its actual Compose source mounted read-only. Run setup-http-mcp.sh with --compose-file and --project on the Docker host; the launcher supplies the source and persistent volumes.");
        }
        var composeOutput = request.ComposeOutputDirectory ?? output;
        if (!Path.IsPathFullyQualified(composeOutput) || composeOutput.Any(char.IsControl))
            throw new AgentClientValidationException("ComposeOutputDirectory must be the explicit absolute host path of the persistent setup output mount.");
        var address = InstanceSetupAddress.Parse(request.PublicOrigin);
        var origin = address.Origin;
        var api = string.IsNullOrWhiteSpace(request.ApiBaseUrl) ? origin : Https(request.ApiBaseUrl, "ApiBaseUrl").AbsoluteUri.TrimEnd('/');
        var resource = request.PublicResourceUri ?? address.McpResource;
        var resourceUri = Https(resource, "PublicResourceUri");
        if (!resourceUri.AbsolutePath.EndsWith("/mcp", StringComparison.Ordinal))
            throw new AgentClientValidationException("PublicResourceUri must end in /mcp.");
        var resourceAuthority = resource[..resource.IndexOf('/', resource.IndexOf("://", StringComparison.Ordinal) + 3)];
        resource = resourceUri.AbsoluteUri.TrimEnd('/');
        if (resourceUri.IsDefaultPort && resourceAuthority.EndsWith(":443", StringComparison.Ordinal))
            resource = resourceUri.GetLeftPart(UriPartial.Authority) + ":443" + resourceUri.AbsolutePath.TrimEnd('/');
        if (request.Instance is not ("dev" or "prod")) throw new AgentClientValidationException("Instance must be dev or prod.");
        if (request.ListeningPort is < 1024 or > 65535) throw new AgentClientValidationException("ListeningPort must be between 1024 and 65535.");
        if (request.DelegationLifetimeSeconds is < 30 or > 300) throw new AgentClientValidationException("DelegationLifetimeSeconds must be between 30 and 300.");
        var webApi = Backend(request.WebApiBackendUrl, "WebApiBackendUrl");
        var webMcp = Backend(request.WebMcpBackendUrl == "http://mcp-http:9224/"
            ? $"http://mcp-http:{request.ListeningPort}/"
            : request.WebMcpBackendUrl, "WebMcpBackendUrl");
        var groups = request.RequiredGroups ?? [];
        var scopes = request.RequiredScopes ?? [];
        if (string.IsNullOrWhiteSpace(request.ApiServiceAudience) || request.ApiServiceAudience.Length > 128 ||
            request.ApiServiceAudience.Any(char.IsWhiteSpace) || request.ApiServiceAudience.Any(char.IsControl) ||
            request.ApiServiceAudience.Contains('$') || request.ApiServiceAudience.Contains('\''))
            throw new AgentClientValidationException("The existing API service audience must be a nonempty bounded scope token.");
        if (!request.LocalCredentialMode)
        {
            Https(request.OidcAuthority, "OidcAuthority");
            if (groups.Count == 0 || scopes.Count == 0) throw new AgentClientValidationException("OIDC HTTP hosting needs explicit RequiredGroups and RequiredScopes.");
        }
        foreach (var values in new[] { groups, scopes })
            if (values.Count > 32 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsWhiteSpace) || value.Contains('$') || value.Contains('\'')) || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
                throw new AgentClientValidationException("Groups and scopes must be unique nonempty tokens without whitespace or interpolation characters.");
        var requestedOrigins = request.AllowedOrigins ?? [origin];
        if (requestedOrigins.Count > 16) throw new AgentClientValidationException("Hosting setup supports up to 16 explicit allowed origins.");
        // Browser Origin serialization omits the default HTTPS port. Keep the
        // user's explicit authority in setup/resource identity, and emit wire
        // origins only after strict origin-only validation.
        IReadOnlyList<string> origins = requestedOrigins.Select(allowed =>
            Https(allowed, "AllowedOrigins", originOnly: true).GetLeftPart(UriPartial.Authority)).ToArray();
        var secretDirectory = Path.Combine(output, "secrets");
        var delegationPath = Path.Combine(secretDirectory, "delegation.key");
        var serviceSecretPath = Path.Combine(secretDirectory, "outbound-m2m.secret");
        var savedGeneration = File.Exists(Path.Combine(output, ".http-host.setup.json")) || File.Exists(Path.Combine(output, "compose.http-host.yaml"));
        void RequireSavedSecret(string path)
        {
            if (savedGeneration && !File.Exists(path))
                throw new AgentClientValidationException("The saved host generation is missing an original protected secret. Restore that same generation from its protected runtime backup or perform an explicit coordinated rotation; setup will not generate a replacement.");
        }
        string? existingDelegation = null;
        if (request.ExistingDelegationKeyFile is not null)
        {
            if (!Path.IsPathFullyQualified(request.ExistingDelegationKeyFile))
                throw new AgentClientValidationException("ExistingDelegationKeyFile must be an explicit absolute protected path.");
            existingDelegation = Path.GetFullPath(request.ExistingDelegationKeyFile);
            if (!File.Exists(existingDelegation)) throw new AgentClientValidationException("The existing delegation key file does not exist.");
            if (string.IsNullOrWhiteSpace(request.DelegationKeyId) || string.IsNullOrWhiteSpace(request.DelegationIssuer) || string.IsNullOrWhiteSpace(request.DelegationAudience))
                throw new AgentClientValidationException("An existing delegation generation requires its explicit DelegationKeyId, DelegationIssuer and DelegationAudience; setup never infers or rotates these values.");
            ValidateExistingSecret(existingDelegation, ownerOnly: false);
        }
        else { RequireSavedSecret(delegationPath); ValidateExistingSecret(delegationPath); }
        if (request.ExistingOutboundConfigurationPath is null) { RequireSavedSecret(serviceSecretPath); ValidateExistingSecret(serviceSecretPath); }

        var keyId = Identifier(request.DelegationKeyId ?? "deployment-v1", "DelegationKeyId");
        var issuer = Identifier(request.DelegationIssuer ?? "netratel-mcp-" + request.Instance, "DelegationIssuer");
        var audience = Identifier(request.DelegationAudience ?? "netratel-api-" + request.Instance, "DelegationAudience");

        var delegation = new Dictionary<string, string>
        {
            ["NetRatel__Mcp__Delegation__Enabled"] = "true",
            ["NetRatel__Mcp__Delegation__Issuer"] = issuer,
            ["NetRatel__Mcp__Delegation__Audience"] = audience,
            ["NetRatel__Mcp__Delegation__ServicePrincipal"] = ServicePrincipal,
            ["NetRatel__Mcp__Delegation__KeyId"] = keyId,
            ["NetRatel__Mcp__Delegation__LifetimeSeconds"] = request.DelegationLifetimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["NetRatel__Mcp__Delegation__SharedKeyBase64"] = "",
            ["NetRatel__Mcp__Delegation__SharedKeyBase64File"] = SecretMount + "/delegation.key"
        };
        var web = new Dictionary<string, string>
        {
            ["ApiBaseUrl"] = webApi, ["WebGateway__Topology"] = "Compose",
            ["NETRATEL_PUBLIC_ORIGIN"] = origin, ["WebGateway__Mcp__Enabled"] = "true",
            ["WebGateway__Mcp__BackendUrl"] = webMcp,
            ["WebGateway__Mcp__AuthenticationMode"] = request.LocalCredentialMode ? "LocalCredential" : "Oidc"
        };
        if (resourceUri.AbsolutePath != "/mcp")
        {
            web["ReverseProxy__Routes__mcpRoute__Match__Path"] = resourceUri.AbsolutePath;
            web["ReverseProxy__Routes__mcpRoute__Transforms__0__PathSet"] = "/mcp";
        }
        var apiSettings = new Dictionary<string, string>(delegation)
        {
            ["Integration__HttpMcp__PublicResourceUri"] = resource,
            ["Integration__HttpMcp__AuthenticationMode"] = request.LocalCredentialMode ? "LocalCredential" : "Oidc",
            ["Integration__HttpMcp__Authority"] = request.OidcAuthority ?? "",
            ["Branding__SiteUrl"] = origin
        };
        for (var index = 0; index < groups.Count; index++) apiSettings[$"Integration__HttpMcp__RequiredGroups__{index}"] = groups[index];
        for (var index = 0; index < scopes.Count; index++) apiSettings[$"Integration__HttpMcp__RequiredScopes__{index}"] = scopes[index];
        var mcp = new Dictionary<string, string>(delegation)
        {
            ["ASPNETCORE_URLS"] = $"http://+:{request.ListeningPort}",
            ["NETRATEL_MCP_INSTANCE"] = request.Instance,
            ["NETRATEL_CLIENT_DIRECTORY"] = "/var/lib/netratel/clients",
            ["NETRATEL_CLIENT_VOLUME"] = VolumeIdentifier(Environment.GetEnvironmentVariable("NETRATEL_CLIENT_VOLUME") ?? "netratel-http-client-data"),
            [$"NETRATEL_MCP_{request.Instance.ToUpperInvariant()}_API_BASE_URL"] = api,
            ["NetRatel__Mcp__Http__PublicResourceUri"] = resource,
            ["NetRatel__Mcp__Http__LocalCredentialMode"] = request.LocalCredentialMode.ToString().ToLowerInvariant(),
            ["NetRatel__Mcp__Http__OperatorSurfaceEnabled"] = (request.OperatorSurfaceEnabled ?? request.Instance == "prod").ToString().ToLowerInvariant()
        };
        for (var index = 0; index < origins.Count; index++) mcp[$"NetRatel__Mcp__Http__AllowedOrigins__{index}"] = origins[index];
        if (!request.LocalCredentialMode)
        {
            mcp["NetRatel__Mcp__Http__Authority"] = request.OidcAuthority!;
            mcp["NetRatel__Mcp__Http__Audience"] = resource;
            for (var index = 0; index < groups.Count; index++) mcp[$"NetRatel__Mcp__Http__RequiredGroups__{index}"] = groups[index];
            for (var index = 0; index < scopes.Count; index++) mcp[$"NetRatel__Mcp__Http__RequiredScopes__{index}"] = scopes[index];
        }
        string? legacyPath = null;
        IReadOnlyList<string>? effectiveCallerIds = request.ExistingM2MCallerClientIds;
        var legacySecrets = new List<string>();
        AgentClientConfiguration? legacyConfiguration = null;
        string? adoptedLocalM2MSecret = null;
        var requiresRegistrationReplacement = false;
        if (request.ExistingOutboundConfigurationPath is not null)
        {
            legacyPath = Path.GetFullPath(request.ExistingOutboundConfigurationPath);
            var configuration = NetRatelConfigurationService.ReadConfiguration(legacyPath);
            legacyConfiguration = configuration;
            var resolved = NetRatelConfigurationService.ResolveSecretReferences(configuration);
            var validation = new AgentClientConfigurationValidator(ConfigurationSurface.HttpMcpOutbound).Validate(null, resolved);
            if (validation.Failed) throw new AgentClientValidationException(string.Join(" ", validation.Failures));
            if (!NetRatelConfigurationService.Equivalent(configuration.ApiBaseUrl, api))
                throw new AgentClientValidationException("Existing outbound configuration must match the selected HTTPS API target; setup never rebinds its identity.");
            if (!string.IsNullOrWhiteSpace(resolved.Instance) && !string.Equals(resolved.Instance, request.Instance, StringComparison.OrdinalIgnoreCase))
                throw new AgentClientValidationException("Existing outbound configuration Instance must match the selected dev/prod instance; setup never rebinds it.");
            foreach (var reference in new[] { configuration.OidcAppPasswordFile, configuration.ApiM2MClientSecretFile }.OfType<string>())
                legacySecrets.Add(reference); // Preserve the JSON's absolute references with explicit read-only mounts.
            mcp["NETRATEL_MCP_CONFIG"] = SecretMount + "/mcp-outbound.json";
            mcp["NetRatel__Mcp__Http__ConfigurationPath"] = SecretMount + "/mcp-outbound.json";
            var principal = resolved.ApiM2MClientId ?? request.DelegationServicePrincipal;
            if (string.IsNullOrWhiteSpace(principal))
                throw new AgentClientValidationException("Existing OIDC outbound setup needs an explicit DelegationServicePrincipal matching the issued service token subject; its username does not establish that identity.");
            if (request.DelegationServicePrincipal is not null && request.DelegationServicePrincipal != principal)
                throw new AgentClientValidationException("DelegationServicePrincipal must match the existing API M2M client identity.");
            Identifier(principal, "DelegationServicePrincipal");
            delegation["NetRatel__Mcp__Delegation__ServicePrincipal"] = principal;
            apiSettings["NetRatel__Mcp__Delegation__ServicePrincipal"] = delegation["NetRatel__Mcp__Delegation__ServicePrincipal"];
            mcp["NetRatel__Mcp__Delegation__ServicePrincipal"] = delegation["NetRatel__Mcp__Delegation__ServicePrincipal"];
            // Supplying an outbound file does not prove its service registration was
            // provisioned. A reviewed local API registration can reuse exactly those
            // bytes, while an external issuer remains managed by its existing owner.
            if (request.ApiServiceAuthority is not null && !string.IsNullOrWhiteSpace(resolved.ApiM2MClientSecret) &&
                NetRatelConfigurationService.Equivalent(resolved.ApiM2MTokenUrl, new Uri(new Uri(api), "/connect/token").AbsoluteUri))
            {
                Https(request.ApiServiceAuthority, "ApiServiceAuthority");
                if (!string.Equals(resolved.ApiM2MScope, request.ApiServiceAudience, StringComparison.Ordinal))
                    throw new AgentClientValidationException("The existing outbound service scope differs from the API's discovered service audience. Review that deployment's service registration; setup will not broaden or rebind it.");
                var callerIds = (request.ExistingM2MCallerClientIds ?? throw new AgentClientValidationException("Discover the API's existing allowed service callers before adopting a local outbound registration."))
                    .Append(principal).Distinct(StringComparer.Ordinal).ToArray();
                requiresRegistrationReplacement = request.ExistingM2MCallerClientIds!.Contains(principal, StringComparer.Ordinal);
                ValidateCallerIds(callerIds);
                effectiveCallerIds = callerIds;
                var alias = new string(principal.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_').ToArray());
                if (alias.Contains("__", StringComparison.Ordinal))
                    throw new AgentClientValidationException("The existing service identity cannot be represented by an unambiguous deployment registration alias. Keep that identity and manage its API registration through the existing protected configuration; setup will not rename or rotate it.");
                for (var index = 0; index < callerIds.Length; index++) apiSettings[$"M2M__AllowedCallerClientIds__{index}"] = callerIds[index];
                apiSettings[$"M2MClients__{alias}__SecretFile"] = SecretMount + "/adopted-outbound-m2m.secret";
                apiSettings[$"M2MClients__{alias}__Secret"] = "";
                apiSettings[$"M2MClients__{alias}__AllowedAudiences__0"] = request.ApiServiceAudience;
                apiSettings[$"M2MClients__{alias}__AllowedScopes__0"] = request.ApiServiceAudience;
                adoptedLocalM2MSecret = resolved.ApiM2MClientSecret;
            }
        }
        else
        {
            if (request.DelegationServicePrincipal is not null && request.DelegationServicePrincipal != ServicePrincipal)
                throw new AgentClientValidationException("A newly provisioned outbound identity uses netratel-mcp-http. To preserve another service principal, select its existing outbound configuration.");
            // Global issuer/audience belong to the existing API deployment. Register
            // only this service under its effective discovered audience; do not rebind
            // unrelated callers or their token-validation configuration.
            var serviceAudience = request.ApiServiceAudience;
            var callerIds = (request.ExistingM2MCallerClientIds ?? ["netratel.cli", "netratel.client.artifacts"])
                .Append(ServicePrincipal).Distinct(StringComparer.Ordinal).ToArray();
            ValidateCallerIds(callerIds);
            effectiveCallerIds = callerIds;
            for (var index = 0; index < callerIds.Length; index++) apiSettings[$"M2M__AllowedCallerClientIds__{index}"] = callerIds[index];
            apiSettings[$"M2MClients__{RegistrationEnvironmentAlias}__SecretFile"] = SecretMount + "/outbound-m2m.secret";
            apiSettings[$"M2MClients__{RegistrationEnvironmentAlias}__Secret"] = "";
            apiSettings[$"M2MClients__{RegistrationEnvironmentAlias}__AllowedAudiences__0"] = serviceAudience;
            apiSettings[$"M2MClients__{RegistrationEnvironmentAlias}__AllowedScopes__0"] = serviceAudience;
            mcp["NETRATEL_MCP_CONFIG"] = "";
            mcp["NetRatel__Mcp__Http__ConfigurationPath"] = "";
            mcp["NetRatel__Mcp__Outbound__AuthenticationMode"] = "ApiM2MClientSecret";
            mcp["NetRatel__Mcp__Outbound__ApiBaseUrl"] = api;
            mcp["NetRatel__Mcp__Outbound__ApiM2MTokenUrl"] = new Uri(new Uri(api), "/connect/token").AbsoluteUri;
            mcp["NetRatel__Mcp__Outbound__ApiM2MClientId"] = ServicePrincipal;
            mcp["NetRatel__Mcp__Outbound__ApiM2MClientSecretFile"] = SecretMount + "/outbound-m2m.secret";
            mcp["NetRatel__Mcp__Outbound__ApiM2MScope"] = serviceAudience;
            foreach (var alternative in new[] { "OidcTokenUrl", "OidcClientId", "OidcUsername", "OidcAppPassword", "OidcAppPasswordFile", "OidcScope", "IntegrationCredential", "IntegrationCredentialFile" })
                mcp["NetRatel__Mcp__Outbound__" + alternative] = "";
        }
        var runtime = Path.Combine(output, "runtime");
        var runtimeSecrets = new Dictionary<string, string>
        {
            [Path.Combine(runtime, "delegation.key")] = existingDelegation ?? delegationPath,
            [Path.Combine(runtime, legacyPath is null ? "outbound-m2m.secret" : "mcp-outbound.json")] = legacyPath ?? serviceSecretPath
        };
        var useVolume = HttpHostingStorage.UsesValidatedSetupVolume(output);
        if (useVolume) VolumeIdentifier(HttpHostingStorage.VolumeName);
        var runtimeContent = new Dictionary<string, string>();
        var extraMounts = new Dictionary<string, string>();
        if (adoptedLocalM2MSecret is not null)
        {
            var export = Path.Combine(runtime, "adopted-outbound-m2m.secret");
            runtimeSecrets[export] = legacyPath!;
            runtimeContent[export] = adoptedLocalM2MSecret + "\n";
            if (!useVolume) extraMounts[Path.Combine(composeOutput, "runtime", "adopted-outbound-m2m.secret")] = SecretMount + "/adopted-outbound-m2m.secret";
        }
        if (legacyConfiguration is not null)
        {
            var exported = legacyConfiguration;
            var index = 0;
            foreach (var source in legacySecrets.Distinct(StringComparer.Ordinal))
            {
                var name = "outbound-secret-" + index++;
                var target = (useVolume ? "/run/netratel-http-host/runtime/" : SecretMount + "/") + name;
                runtimeSecrets[Path.Combine(runtime, name)] = source;
                extraMounts[Path.Combine(composeOutput, "runtime", name)] = target;
                if (source == legacyConfiguration.ApiM2MClientSecretFile) exported = exported with { ApiM2MClientSecretFile = target };
                if (source == legacyConfiguration.OidcAppPasswordFile) exported = exported with { OidcAppPasswordFile = target };
            }
            runtimeContent[Path.Combine(runtime, "mcp-outbound.json")] = JsonSerializer.Serialize(exported, AgentClientConfiguration.JsonOptions) + "\n";
        }
        if (useVolume)
        {
            foreach (var settings in new[] { apiSettings, mcp })
                foreach (var name in settings.Keys.ToArray())
                    settings[name] = settings[name].Replace(SecretMount + "/", "/run/netratel-http-host/runtime/", StringComparison.Ordinal);
        }
        var reviewedInputs = new List<string>();
        string? komodoCompose = null;
        HttpHostingWebBackendSelection? selectedWebBackends = null;
        if (request.DeploymentManager == "komodo")
        {
            var sourcePath = Path.GetFullPath(request.ComposeSourcePath!);
            if (sourcePath != Path.Combine(output, ".compose-source.yaml"))
                throw new AgentClientValidationException("Komodo setup uses the launcher's private persistent Compose snapshot. Run setup-http-mcp.sh on the Docker host; it reads the actual source without changing its ownership or permissions.");
            var source = ConfigurationFileStorage.ReadText(sourcePath, HttpHostingComposeDocument.MaximumSourceBytes, secret: true);
            var webEnvironmentObservedNow = string.Equals(Environment.GetEnvironmentVariable("NETRATEL_SETUP_WEB_ENVIRONMENT_OBSERVED"), "true", StringComparison.OrdinalIgnoreCase);
            selectedWebBackends = HttpHostingComposeDocument.SelectWebBackends(source, web,
                preserveApi: request.WebApiBackendUrl == "http://api:9222/",
                preserveMcp: request.WebMcpBackendUrl == "http://mcp-http:9224/",
                observedApi: webEnvironmentObservedNow ? Environment.GetEnvironmentVariable("NETRATEL_SETUP_EXISTING_WEB_API_BACKEND_URL") : request.ExistingWebApiBackendUrl,
                observedMcp: webEnvironmentObservedNow ? Environment.GetEnvironmentVariable("NETRATEL_SETUP_EXISTING_WEB_MCP_BACKEND_URL") : request.ExistingWebMcpBackendUrl,
                environmentObserved: webEnvironmentObservedNow || request.ExistingWebEnvironmentObserved);
            web = selectedWebBackends.Changes;
            // Topology is deployment-owned. Enabling this route must not reset a
            // working SplitHost or LocalProcess Web/API arrangement to Compose.
            web.Remove("WebGateway__Topology");
            var hostSource = Environment.GetEnvironmentVariable("NETRATEL_SETUP_COMPOSE_FILES");
            if (string.IsNullOrWhiteSpace(hostSource) || !Path.IsPathFullyQualified(hostSource) || hostSource.IndexOfAny([',', '$', '\'']) >= 0 || hostSource.Any(char.IsControl))
                throw new AgentClientValidationException("Komodo setup needs the actual absolute Compose filename from setup-http-mcp.sh. Run the launcher on the Docker host with --compose-file; no assumed setup file is used.");
            mcp["NETRATEL_SETUP_DIRECTORY"] = "/setup-output";
            mcp["NETRATEL_SETUP_VOLUME"] = HttpHostingStorage.VolumeName;
            mcp["NETRATEL_SETUP_DEPLOYMENT_MANAGER"] = "komodo";
            mcp["NETRATEL_SETUP_COMPOSE_FILES"] = hostSource;
            mcp["NETRATEL_SETUP_COMPOSE_SOURCE"] = sourcePath;
            var project = Environment.GetEnvironmentVariable("NETRATEL_SETUP_PROJECT_NAME");
            if (!string.IsNullOrEmpty(project))
            {
                if (project.Length > 128 || project.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
                    throw new AgentClientValidationException("The setup project name must be the actual bounded Compose project identifier supplied by the launcher.");
                mcp["NETRATEL_SETUP_PROJECT_NAME"] = project;
            }
            komodoCompose = HttpHostingComposeDocument.Merge(source, web, apiSettings, mcp, HttpHostingStorage.RuntimeGroup,
                HttpHostingStorage.VolumeName, mcp["NETRATEL_CLIENT_VOLUME"], request.ListeningPort, project);
            reviewedInputs.Add(sourcePath);
        }
        var configurationFiles = new Dictionary<string, string>
        {
            [Path.Combine(output, "web.env")] = EnvironmentFile(web),
            [Path.Combine(output, "api.env")] = EnvironmentFile(apiSettings),
            [Path.Combine(output, "mcp-http.env")] = EnvironmentFile(mcp),
            [Path.Combine(output, "compose.http-host.yaml")] = Overlay(composeOutput,
                Path.Combine(composeOutput, "runtime", "delegation.key"),
                Path.Combine(composeOutput, "runtime", legacyPath is null ? "outbound-m2m.secret" : "mcp-outbound.json"),
                legacyPath is not null, extraMounts, web, apiSettings, mcp, request.ListeningPort, useVolume),
            [Path.Combine(output, ".http-host.setup.json")] = JsonSerializer.Serialize(request with
            {
                OutputDirectory = output, PublicOrigin = origin, ApiBaseUrl = api, PublicResourceUri = resource,
                ComposeOutputDirectory = composeOutput, ApprovedRegistrationFingerprint = null,
                RequiredGroups = groups.ToArray(), RequiredScopes = scopes.ToArray(), AllowedOrigins = origins.ToArray(),
                ExistingM2MCallerClientIds = effectiveCallerIds?.ToArray(),
                ExistingWebApiBackendUrl = selectedWebBackends?.ApiBackend ?? request.ExistingWebApiBackendUrl,
                ExistingWebMcpBackendUrl = selectedWebBackends?.McpBackend ?? request.ExistingWebMcpBackendUrl,
                ExistingWebEnvironmentObserved = request.ExistingWebEnvironmentObserved || string.Equals(Environment.GetEnvironmentVariable("NETRATEL_SETUP_WEB_ENVIRONMENT_OBSERVED"), "true", StringComparison.OrdinalIgnoreCase)
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            [Path.Combine(output, ".gitignore")] = "secrets/\nruntime/\n*.env\ncompose.komodo.yaml\n.http-host.setup.json\n"
        };
        if (komodoCompose is not null)
        {
            // Komodo imports one complete managed document. Release-only overlays
            // and env files are unused here and would change shape after import,
            // even while the effective complete document and identity stay equal.
            foreach (var unused in new[] { "web.env", "api.env", "mcp-http.env", "compose.http-host.yaml" })
                configurationFiles.Remove(Path.Combine(output, unused));
            configurationFiles[Path.Combine(output, "compose.komodo.yaml")] = komodoCompose;
        }
        var instructions = new[]
        {
            "This bundle prepares configuration only; deployment apply and live verification remain pending.",
            (request.DeploymentManager == "komodo" ? "Apply through the deployment manager: " : "On the deployment host, review and run: ") + ApplyCommand(request),
            request.DeploymentManager == "komodo"
                ? "setup-http-mcp.sh exports the complete private compose.komodo.yaml without printing it. Import that file into the existing stack's Komodo Compose editor; its original ingress, database, ports and environment-file sources remain included. Do not paste secret values or edit YAML fields by hand."
                : "The complete overlay supplies effective Web/API/MCP settings. Independent owner-only host secrets and protected runtime exports reuse the same generation; API UID 1654 and MCP UID 10001 receive explicit read-only access.",
            "The API recreation provisions the dedicated outbound service registration and matching delegation. A personal verification credential is never the outbound host identity.",
            "Resume setup with the same instance URL after recreation, then Verify. Only authenticated initialize/catalog and a permitted read-only API-backed tool result establish end-to-end readiness.",
            request.DeploymentManager == "komodo"
                ? "Keep both named persistent volumes. After Save/Deploy, rerun the same setup-http-mcp.sh command with --verify. Existing supplied settings are adopted inputs; only successful authenticated checks establish readiness."
                : "Keep approved image digests and ingress overlays in NETRATEL_SETUP_COMPOSE_FILES (comma-separated files) when they differ from the supplied default. Rerunning setup preserves the existing identity generation."
        };
        var generatedSecrets = new List<string>();
        if (existingDelegation is null) generatedSecrets.Add(delegationPath);
        if (legacyPath is null) generatedSecrets.Add(serviceSecretPath);
        var inputPaths = new List<string>(legacySecrets);
        if (legacyPath is not null) inputPaths.Add(legacyPath);
        if (existingDelegation is not null) inputPaths.Add(existingDelegation);
        return new(configurationFiles, generatedSecrets.ToArray(), inputPaths.ToArray(), reviewedInputs.ToArray(), runtimeSecrets, runtimeContent,
            delegation["NetRatel__Mcp__Delegation__ServicePrincipal"],
            web.Select(pair => $"Web: {pair.Key} = {pair.Value}")
                .Concat(apiSettings.Select(pair => $"API: {pair.Key} = {pair.Value}"))
                .Concat(mcp.Select(pair => $"HTTP MCP: {pair.Key} = {pair.Value}"))
                .Append($"Runtime secret readers: reviewed container GID {HttpHostingStorage.RuntimeGroup}; " +
                    (useVolume ? "supplied private setup volume (owner/group access only)." : "native output boundary remains owner-only (0700); the local group cannot traverse it."))
                .Concat(selectedWebBackends is null ? [] : new[]
                {
                    "Web private API backend: " + selectedWebBackends.ApiBackend + (web.ContainsKey("ApiBaseUrl") ? " (reviewed change)." : " (existing deployment source retained)."),
                    "Web private MCP backend: " + selectedWebBackends.McpBackend + (web.ContainsKey("WebGateway__Mcp__BackendUrl") ? " (reviewed change)." : " (existing deployment source retained).")
                })
                .Concat(requiresRegistrationReplacement
                    ? ["Replace API service registration for " + delegation["NetRatel__Mcp__Delegation__ServicePrincipal"] + " using the selected existing credential; the current API secret cannot be compared here. Explicit review is required; other registrations remain unchanged."]
                    : [])
                .ToArray(), instructions, requiresRegistrationReplacement);
    }

    private static string EnvironmentFile(Dictionary<string, string> values)
        => "# Prepared HTTP MCP deployment configuration; secret values are separate.\n" +
           string.Join('\n', values.Select(pair => pair.Key + "='" + pair.Value + "'")) + "\n";

    private static string RequestSignature(HttpHostingSetupRequest request)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));

    private static string Overlay(string output, string delegation, string outbound, bool legacy, IReadOnlyDictionary<string, string> extraMounts,
        Dictionary<string, string> web, Dictionary<string, string> api, Dictionary<string, string> mcp, int listeningPort, bool setupVolume)
    {
        string Quote(string value) => JsonSerializer.Serialize(value.Replace("$", "$$", StringComparison.Ordinal));
        string Mount(string path, string target) => $"      - type: bind\n        source: {Quote(path)}\n        target: {Quote(target)}\n        read_only: true\n        bind:\n          create_host_path: false\n";
        var key = setupVolume ? "      - type: volume\n        source: http-host-setup\n        target: /run/netratel-http-host\n        read_only: true\n" : Mount(delegation, SecretMount + "/delegation.key");
        var identity = setupVolume ? "" : Mount(outbound, SecretMount + (legacy ? "/mcp-outbound.json" : "/outbound-m2m.secret"));
        var client = "      - type: volume\n        source: http-client-data\n        target: /var/lib/netratel/clients\n";
        var group = "    group_add:\n      - " + Quote(HttpHostingStorage.RuntimeGroup.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "\n";
        string EnvironmentMap(Dictionary<string, string> settings, bool replace = false) => "    environment:" + (replace ? " !override\n" : "\n") +
            string.Concat(settings.Select(pair => $"      {Quote(pair.Key)}: {Quote(pair.Value)}\n"));
        // Environment entries take precedence over env_file. Use explicit reviewed
        // values so intervening overlays cannot leave stale authentication defaults.
        // MCP is a complete managed identity boundary; later overrides remain explicit.
        return "# Requires Docker Compose 2.24.4+; apply after compose.mcp-http.yaml.\nservices:\n" +
            $"  web:\n{EnvironmentMap(web)}" +
            $"  api:\n{group}{EnvironmentMap(api)}    volumes:\n{key}" + (legacy ? "" : identity) +
            $"  mcp-http:\n{group}{EnvironmentMap(mcp, replace: true)}    env_file: !override []\n    healthcheck:\n      test: [\"CMD\", \"curl\", \"--fail\", \"--silent\", \"http://127.0.0.1:{listeningPort}/health/live\"]\n    volumes:\n{client}{key}{identity}" +
            (setupVolume ? "" : string.Concat(extraMounts.Select(pair => Mount(pair.Key, pair.Value)))) +
            "volumes:\n  http-client-data:\n    name: " + Quote(mcp["NETRATEL_CLIENT_VOLUME"]) + "\n" +
            (setupVolume ? "  http-host-setup:\n    external: true\n    name: " + Quote(HttpHostingStorage.VolumeName) + "\n" : "");
    }

    private static Uri Https(string? value, string field, bool originOnly = false)
    {
        if (!NetRatelConfigurationService.IsHttps(value) || value!.Length > 2048 || value.Any(char.IsControl) || value.Contains('$') || value.Contains('\'') ||
            originOnly && new Uri(value).AbsolutePath != "/")
            throw new AgentClientValidationException(field + " must be an explicit HTTPS " + (originOnly ? "origin" : "URL") + " without credentials, query or fragment.");
        return new Uri(value!);
    }

    private static string Backend(string value, string field)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            value.Any(char.IsControl) || value.Contains('$') || value.Contains('\''))
            throw new AgentClientValidationException(field + " must be an explicit private HTTP or HTTPS backend URL.");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    private static string Identifier(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':' and not '/'))
            throw new AgentClientValidationException(field + " must be an explicit bounded deployment identity token.");
        return value;
    }

    private static void ValidateCallerIds(string[] callerIds)
    {
        if (callerIds.Length > 128 || callerIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 || !char.IsAsciiLetterOrDigit(id[0]) || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_')) ||
            callerIds.Select(id => new string(id.Select(character => char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_').ToArray())).Distinct(StringComparer.Ordinal).Count() != callerIds.Length)
            throw new AgentClientValidationException("M2M caller IDs must be explicit unambiguous client identity tokens.");
    }

    private static string VolumeIdentifier(string value)
    {
        if (value.Length is < 1 or > 255 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new AgentClientValidationException("Persistent volume names must be explicit Docker volume identifiers selected by the deployment launcher.");
        return value;
    }

    private static void ValidateExistingSecret(string path, bool ownerOnly = true)
    {
        ConfigurationFileStorage.EnsureSafePath(path);
        if (!File.Exists(path)) return;
        var value = ConfigurationFileStorage.ReadText(path, 16_384, secret: true).Trim();
        try
        {
            if (Convert.FromBase64String(value).Length < 32) throw new FormatException();
        }
        catch (FormatException) { throw new AgentClientValidationException("Existing deployment signing/service secret is invalid; repair or explicitly rotate it before setup. It will not be overwritten."); }
        if (ownerOnly && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
            throw new AgentClientValidationException("Existing setup secrets must be owner-only. Use a protected secret-store export for runtime shared-group access.");
    }

    private sealed record Bundle(Dictionary<string, string> Configuration, string[] SecretPaths, string[] InputPaths, string[] ReviewedInputPaths,
        Dictionary<string, string> RuntimeSecrets, Dictionary<string, string> RuntimeContent, string ServicePrincipal,
        IReadOnlyList<string> SelectedSettings, IReadOnlyList<string> Instructions, bool RequiresRegistrationReplacement);
}

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
    string? DelegationAudience = null);

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
    private const string SecretMount = "/run/netratel-mcp-secrets";
    private string? _reviewedRequestSignature;
    private Dictionary<string, string?>? _reviewedHashes;

    public HttpHostingSetupPreview Preview(HttpHostingSetupRequest request)
    {
        var bundle = Prepare(request);
        var changes = bundle.Configuration.Select(file =>
            !File.Exists(file.Key) ? $"Create {file.Key} (deployment setup)" :
            ConfigurationFileStorage.ReadText(file.Key) == file.Value ? $"Reuse {file.Key} (deployment setup)" :
            $"Replace {file.Key} (review required; existing file or externally managed values)").ToList();
        changes.AddRange(bundle.SelectedSettings);
        changes.Add("The final overlay supplies reviewed Web/API settings and replaces the managed MCP environment and env-file list; retain additional MCP runtime options in a later explicit overlay.");
        foreach (var path in bundle.SecretPaths)
            changes.Add(File.Exists(path) ? $"Reuse protected secret reference {path}" : $"Create protected secret reference {path} (value withheld)");
        foreach (var path in bundle.InputPaths)
            changes.Add($"Reuse existing reviewed input {path} (read-only reference; values withheld)");
        _reviewedRequestSignature = RequestSignature(request);
        _reviewedHashes = bundle.Configuration.Keys.Concat(bundle.SecretPaths).Concat(bundle.InputPaths).Distinct(StringComparer.Ordinal)
            .ToDictionary(path => path, path => File.Exists(path) ? ConfigurationFileStorage.Hash(path) : null, StringComparer.Ordinal);
        return new(changes, bundle.Configuration.Keys.Concat(bundle.SecretPaths).ToArray(), bundle.Instructions,
            bundle.Configuration.Keys.Any(File.Exists));
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
        foreach (var path in bundle.SecretPaths)
        {
            if (!File.Exists(path))
                ConfigurationFileStorage.WriteAtomic(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");
        }
        foreach (var file in writes)
            ConfigurationFileStorage.WriteAtomic(file.Key, file.Value, replace, file.Before ?? string.Empty);
        _reviewedHashes = null;
        _reviewedRequestSignature = null;
        return new(bundle.Configuration.Keys.Concat(bundle.SecretPaths).ToArray(), bundle.Instructions);
    }

    private static Bundle Prepare(HttpHostingSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OutputDirectory) || !Path.IsPathFullyQualified(request.OutputDirectory) || request.OutputDirectory.Any(char.IsControl))
            throw new AgentClientValidationException("HTTP hosting setup requires an explicit absolute writable output directory (a persistent mount in containers).");
        var output = Path.GetFullPath(request.OutputDirectory);
        ConfigurationFileStorage.EnsureSafePath(output);
        var composeOutput = request.ComposeOutputDirectory ?? output;
        if (!Path.IsPathFullyQualified(composeOutput) || composeOutput.Any(char.IsControl))
            throw new AgentClientValidationException("ComposeOutputDirectory must be the explicit absolute host path of the persistent setup output mount.");
        var origin = Https(request.PublicOrigin, "PublicOrigin", originOnly: true).GetLeftPart(UriPartial.Authority);
        var api = Https(request.ApiBaseUrl, "ApiBaseUrl").AbsoluteUri.TrimEnd('/');
        var resource = request.PublicResourceUri ?? origin + "/mcp";
        var resourceUri = Https(resource, "PublicResourceUri");
        if (!resourceUri.AbsolutePath.EndsWith("/mcp", StringComparison.Ordinal))
            throw new AgentClientValidationException("PublicResourceUri must end in /mcp.");
        resource = resourceUri.AbsoluteUri.TrimEnd('/');
        if (request.Instance is not ("dev" or "prod")) throw new AgentClientValidationException("Instance must be dev or prod.");
        if (request.ListeningPort is < 1024 or > 65535) throw new AgentClientValidationException("ListeningPort must be between 1024 and 65535.");
        var webApi = Backend(request.WebApiBackendUrl, "WebApiBackendUrl");
        var webMcp = Backend(request.WebMcpBackendUrl == "http://mcp-http:9224/"
            ? $"http://mcp-http:{request.ListeningPort}/"
            : request.WebMcpBackendUrl, "WebMcpBackendUrl");
        var groups = request.RequiredGroups ?? [];
        var scopes = request.RequiredScopes ?? [];
        if (!request.LocalCredentialMode)
        {
            Https(request.OidcAuthority, "OidcAuthority");
            if (groups.Count == 0 || scopes.Count == 0) throw new AgentClientValidationException("OIDC HTTP hosting needs explicit RequiredGroups and RequiredScopes.");
        }
        foreach (var values in new[] { groups, scopes })
            if (values.Count > 32 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsWhiteSpace) || value.Contains('$') || value.Contains('\'')) || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
                throw new AgentClientValidationException("Groups and scopes must be unique nonempty tokens without whitespace or interpolation characters.");
        var origins = request.AllowedOrigins ?? [origin];
        if (origins.Count > 16) throw new AgentClientValidationException("Hosting setup supports up to 16 explicit allowed origins.");
        foreach (var allowed in origins) Https(allowed, "AllowedOrigins", originOnly: true);
        var secretDirectory = Path.Combine(output, "secrets");
        var delegationPath = Path.Combine(secretDirectory, "delegation.key");
        var serviceSecretPath = Path.Combine(secretDirectory, "outbound-m2m.secret");
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
        else ValidateExistingSecret(delegationPath);
        if (request.ExistingOutboundConfigurationPath is null) ValidateExistingSecret(serviceSecretPath);

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
            ["Branding__SiteUrl"] = origin
        };
        var mcp = new Dictionary<string, string>(delegation)
        {
            ["ASPNETCORE_URLS"] = $"http://+:{request.ListeningPort}",
            ["NETRATEL_MCP_INSTANCE"] = request.Instance,
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
        var legacySecrets = new List<string>();
        if (request.ExistingOutboundConfigurationPath is not null)
        {
            legacyPath = Path.GetFullPath(request.ExistingOutboundConfigurationPath);
            var configuration = NetRatelConfigurationService.ReadConfiguration(legacyPath);
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
        }
        else
        {
            if (request.DelegationServicePrincipal is not null && request.DelegationServicePrincipal != ServicePrincipal)
                throw new AgentClientValidationException("A newly provisioned outbound identity uses netratel-mcp-http. To preserve another service principal, select its existing outbound configuration.");
            apiSettings["M2M__Authority"] = new Uri(api).GetLeftPart(UriPartial.Authority);
            apiSettings["M2M__Audience"] = "netratel.api";
            var callerIds = (request.ExistingM2MCallerClientIds ?? ["netratel.cli", "netratel.client.artifacts"])
                .Append(ServicePrincipal).Distinct(StringComparer.Ordinal).ToArray();
            if (callerIds.Length > 128 || callerIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_')))
                throw new AgentClientValidationException("M2M caller IDs must be explicit nonempty client identity tokens.");
            for (var index = 0; index < callerIds.Length; index++) apiSettings[$"M2M__AllowedCallerClientIds__{index}"] = callerIds[index];
            apiSettings[$"M2MClients__{ServicePrincipal}__SecretFile"] = SecretMount + "/outbound-m2m.secret";
            apiSettings[$"M2MClients__{ServicePrincipal}__Secret"] = "";
            apiSettings[$"M2MClients__{ServicePrincipal}__AllowedAudiences__0"] = "netratel.api";
            apiSettings[$"M2MClients__{ServicePrincipal}__AllowedScopes__0"] = "netratel.api";
            mcp["NETRATEL_MCP_CONFIG"] = "";
            mcp["NetRatel__Mcp__Http__ConfigurationPath"] = "";
            mcp["NetRatel__Mcp__Outbound__AuthenticationMode"] = "ApiM2MClientSecret";
            mcp["NetRatel__Mcp__Outbound__ApiBaseUrl"] = api;
            mcp["NetRatel__Mcp__Outbound__ApiM2MTokenUrl"] = new Uri(new Uri(api), "/connect/token").AbsoluteUri;
            mcp["NetRatel__Mcp__Outbound__ApiM2MClientId"] = ServicePrincipal;
            mcp["NetRatel__Mcp__Outbound__ApiM2MClientSecretFile"] = SecretMount + "/outbound-m2m.secret";
            mcp["NetRatel__Mcp__Outbound__ApiM2MScope"] = "netratel.api";
            foreach (var alternative in new[] { "OidcTokenUrl", "OidcClientId", "OidcUsername", "OidcAppPassword", "OidcAppPasswordFile", "OidcScope", "IntegrationCredential", "IntegrationCredentialFile" })
                mcp["NetRatel__Mcp__Outbound__" + alternative] = "";
        }
        var configurationFiles = new Dictionary<string, string>
        {
            [Path.Combine(output, "web.env")] = EnvironmentFile(web),
            [Path.Combine(output, "api.env")] = EnvironmentFile(apiSettings),
            [Path.Combine(output, "mcp-http.env")] = EnvironmentFile(mcp),
            [Path.Combine(output, "compose.http-host.yaml")] = Overlay(composeOutput,
                existingDelegation ?? Path.Combine(composeOutput, "secrets", "delegation.key"),
                legacyPath ?? Path.Combine(composeOutput, "secrets", "outbound-m2m.secret"),
                legacyPath is not null, legacySecrets, web, apiSettings, mcp, request.ListeningPort),
            [Path.Combine(output, ".gitignore")] = "secrets/\n*.env\n"
        };
        var instructions = new[]
        {
            "The bundle is prepared only; Web, API and MCP containers must be recreated to apply it.",
            $"Use the base Compose file, compose.mcp-http.yaml, and {Path.Combine(output, "compose.http-host.yaml")} together; keep the deployment's approved image digests and HTTPS ingress settings.",
            "Generated env files are actual-key exports. The final overlay supplies the reviewed effective values; reflect manual setting changes there, then recreate. Web/API configuration is additive; MCP's complete managed environment replaces older identity sources. Later explicit overlays can supply additional runtime options.",
            "Secret originals are owner-only. Before applying, set the API and MCP bind source paths to their respective readable secret-store exports of the SAME generation, keeping container targets unchanged and mounts read-only; use restricted ownership/group access. Do not make secrets world-readable.",
            "The API environment provisions the outbound service registration only after API recreation; pairing a human client does not provision that identity.",
            "Verify configuration, TLS/reachability, route selection, authentication/delegation, MCP initialize, then one permitted read-only tool call. Setup does not claim these live checks passed.",
            "Rotate service credentials and delegation material explicitly with coordinated API/MCP updates; rerunning setup preserves the existing generation."
        };
        var generatedSecrets = new List<string>();
        if (existingDelegation is null) generatedSecrets.Add(delegationPath);
        if (legacyPath is null) generatedSecrets.Add(serviceSecretPath);
        var inputPaths = new List<string>(legacySecrets);
        if (legacyPath is not null) inputPaths.Add(legacyPath);
        if (existingDelegation is not null) inputPaths.Add(existingDelegation);
        return new(configurationFiles, generatedSecrets.ToArray(), inputPaths.ToArray(),
            web.Select(pair => $"Web: {pair.Key} = {pair.Value}")
                .Concat(apiSettings.Select(pair => $"API: {pair.Key} = {pair.Value}"))
                .Concat(mcp.Select(pair => $"HTTP MCP: {pair.Key} = {pair.Value}"))
                .ToArray(), instructions);
    }

    private static string EnvironmentFile(Dictionary<string, string> values)
        => "# Prepared HTTP MCP deployment configuration; secret values are separate.\n" +
           string.Join('\n', values.Select(pair => pair.Key + "='" + pair.Value + "'")) + "\n";

    private static string RequestSignature(HttpHostingSetupRequest request)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));

    private static string Overlay(string output, string delegation, string outbound, bool legacy, IReadOnlyList<string> legacySecrets,
        Dictionary<string, string> web, Dictionary<string, string> api, Dictionary<string, string> mcp, int listeningPort)
    {
        string Quote(string value) => JsonSerializer.Serialize(value.Replace("$", "$$", StringComparison.Ordinal));
        string Mount(string path, string target) => $"      - type: bind\n        source: {Quote(path)}\n        target: {Quote(target)}\n        read_only: true\n        bind:\n          create_host_path: false\n";
        var key = Mount(delegation, SecretMount + "/delegation.key");
        var identity = Mount(outbound, SecretMount + (legacy ? "/mcp-outbound.json" : "/outbound-m2m.secret"));
        string EnvironmentMap(Dictionary<string, string> settings, bool replace = false) => "    environment:" + (replace ? " !override\n" : "\n") +
            string.Concat(settings.Select(pair => $"      {Quote(pair.Key)}: {Quote(pair.Value)}\n"));
        // Environment entries take precedence over env_file. Use explicit reviewed
        // values so intervening overlays cannot leave stale authentication defaults.
        // MCP is a complete managed identity boundary; later overrides remain explicit.
        return "# Requires Docker Compose 2.24.4+; apply after compose.mcp-http.yaml.\nservices:\n" +
            $"  web:\n{EnvironmentMap(web)}    env_file:\n      - {Quote(Path.Combine(output, "web.env"))}\n" +
            $"  api:\n{EnvironmentMap(api)}    env_file:\n      - {Quote(Path.Combine(output, "api.env"))}\n    volumes:\n{key}" + (legacy ? "" : identity) +
            $"  mcp-http:\n{EnvironmentMap(mcp, replace: true)}    env_file: !override\n      - {Quote(Path.Combine(output, "mcp-http.env"))}\n    healthcheck:\n      test: [\"CMD\", \"curl\", \"--fail\", \"--silent\", \"http://127.0.0.1:{listeningPort}/health/live\"]\n    volumes:\n{key}{identity}" +
            string.Concat(legacySecrets.Select(path => Mount(path, path)));
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

    private sealed record Bundle(Dictionary<string, string> Configuration, string[] SecretPaths, string[] InputPaths,
        IReadOnlyList<string> SelectedSettings, IReadOnlyList<string> Instructions);
}

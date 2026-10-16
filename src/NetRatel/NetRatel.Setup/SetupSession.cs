using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.AgentClient;
using NetRatel.Shared.Connectivity;
using PairingCapabilities = NetRatel.Shared.Connectivity.IntegrationPairingCapabilities;
using PairingStart = NetRatel.Shared.Connectivity.IntegrationPairingStartResponse;
using PairingDelivery = NetRatel.Shared.Connectivity.IntegrationPairingDelivery;
using PairingPoll = NetRatel.Shared.Connectivity.IntegrationPairingPollResponse;

namespace NetRatel.Setup;

public enum SetupStep { Instance, Profile, Authentication, Grants, Review, Finish }
public sealed record SetupCheck(string Name, string Status, string Detail, int? HttpStatus = null);
/// <summary>Headless setup operations shared by the TUI and noninteractive entry points.</summary>
public sealed class SetupSession : IAsyncDisposable
{
    // The production HTTP catalog is approximately 331 KiB; keep a bounded
    // response budget that also admits the complete advertised tool schemas.
    private const int MaximumMcpResponseCharacters = 512 * 1024;
    private readonly NetRatelConfigurationService _configuration = new();
    private readonly HttpClient _http;
    private readonly Func<string, string?> _environment;
    private readonly HttpMessageHandler _transport;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _saved;
    private bool _acknowledged;
    private bool _reuseExistingConfiguration;
    private Uri? _origin;
    private string? _canonicalResource;
    private string? _reviewHash;
    private string? _persistedCredentialFile;
    private AgentClientConfiguration _initialConfiguration = AgentClientConfiguration.Empty;
    private IReadOnlyDictionary<string, ConfigurationSettingSource> _initialSources = new Dictionary<string, ConfigurationSettingSource>();
    public SetupSession(ConfigurationSurface surface, string? path = null, string? profile = null, HttpMessageHandler? handler = null, Func<string, string?>? getEnvironmentVariable = null)
    {
        _environment = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _transport = handler ?? new HttpClientHandler { AllowAutoRedirect = false };
        _http = new HttpClient(_transport, disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 262144 };
        Surface = surface;
        profile ??= NetRatelConfigurationService.ReadEnvironment(surface, _environment).ProfileName;
        if (path is null && profile is null && surface is ConfigurationSurface.StdioMcp or ConfigurationSurface.HttpMcpOutbound)
            path = AgentClientConfigurationResolver.GetMcpConfigurationPath(_environment);
        if (path is null && profile is null && surface is ConfigurationSurface.Cli or ConfigurationSurface.AgentClient &&
            string.IsNullOrWhiteSpace(_environment("NETRATEL_CLIENT_DIRECTORY")) && string.IsNullOrWhiteSpace(_environment("NETRATEL_SETUP_DIRECTORY")) && File.Exists(AgentClientConfigurationStore.DefaultPath))
            path = AgentClientConfigurationStore.DefaultPath;
        HasExplicitDestination = path is not null;
        if (path is null)
        {
            profile ??= "default";
            path = NetRatelConfigurationService.ProfilePath(surface, profile, _environment);
        }
        var effective = _configuration.Load(new(surface, path, profile, GetEnvironmentVariable: _environment), allowCreate: true);
        Path = effective.ConfigurationPath ?? NetRatelConfigurationService.ProfilePath(surface, profile ?? "default", _environment);
        ProfileName = effective.ProfileName ?? profile ?? "default";
        Configuration = effective.Configuration;
        Sources = effective.Sources;
        _initialConfiguration = Configuration;
        _initialSources = Sources;
    }
    public bool HasExplicitDestination { get; }
    public bool HasConfiguredAuthentication => new AgentClientConfigurationValidator(Surface).Validate(null, Configuration).Succeeded;
    public ConfigurationSurface Surface { get; private set; }
    public string Path { get; private set; }
    public string ProfileName { get; private set; }
    public AgentClientConfiguration Configuration { get; private set; }
    public IReadOnlyDictionary<string, ConfigurationSettingSource> Sources { get; private set; }
    public PairingCapabilities? Capabilities { get; private set; }
    public PairingStart? Pairing { get; private set; }
    public PairingDelivery? Delivery { get; private set; }
    public string PairingStatus { get; private set; } = "not_started";
    public bool ReusesExistingConfiguration => _reuseExistingConfiguration;
    public bool Saved => _saved && (Pairing is null || Delivery is null || _acknowledged);
    public bool Persisted => _saved;
    public string CanonicalTarget => _origin is null ? "" : Surface == ConfigurationSurface.HttpMcpClient ? _canonicalResource ?? new Uri(_origin, "/mcp").AbsoluteUri : _origin.AbsoluteUri.TrimEnd('/');
    public string CredentialManagementUrl => _origin is null ? "" : new Uri(_origin, "/account/integration-credentials?tab=credentials").AbsoluteUri;
    public void SelectProfile(ConfigurationSurface surface, string profile, string? path = null)
    {
        if (Pairing is not null) throw new AgentClientValidationException("Cancel the current pairing before selecting another profile.");
        if (_saved) throw new InvalidOperationException("Start a new setup invocation after saving.");
        if (profile.Length is < 1 or > 64 || !profile.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new AgentClientValidationException("Profile names use 1–64 letters, digits, dashes or underscores.");
        var selected = _configuration.Load(new(surface, path, profile, GetEnvironmentVariable: _environment), allowCreate: true);
        Surface = surface; ProfileName = profile; Path = selected.ConfigurationPath ?? NetRatelConfigurationService.ProfilePath(surface, profile, _environment);
        Configuration = selected.Configuration; Sources = selected.Sources;
        _initialConfiguration = Configuration; _initialSources = Sources;
        _origin = null; Capabilities = null; Delivery = null; _reuseExistingConfiguration = false;
    }
    public void ValidateDestinationStorage()
    {
        // A selected read-only file remains governed by its source. New or
        // writable profiles must survive the supplied tooling container.
        if (File.Exists(Path) && !ConfigurationFileStorage.IsWritable(Path) || HasConfiguredAuthentication && Sources.Values.Any(source => source.ExternallyManaged && source.IsSecret)) return;
        HttpHostingStorage.ValidateWritable(System.IO.Path.GetDirectoryName(Path)!);
    }
    public bool IsExternallyManaged(string key) => Sources.TryGetValue(key.Length > 0 ? char.ToUpperInvariant(key[0]) + key[1..] : key, out var source) && source.ExternallyManaged;
    public string SourceName(string key) => Sources.TryGetValue(key.Length > 0 ? char.ToUpperInvariant(key[0]) + key[1..] : key, out var source) ? source.Source : "managed source";
    public static Uri ValidateOrigin(string raw) => new(InstanceSetupAddress.Parse(raw).Origin);
    private static Uri ValidateDiscoveredOrigin(string raw)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new AgentClientValidationException("Enter an instance HTTPS origin, for example https://netratel.example.com, without a path, credentials or query.");
        return uri;
    }
    public async Task DiscoverAsync(string raw, CancellationToken ct)
    {
        var origin = ValidateOrigin(raw);
        if (Surface != ConfigurationSurface.HttpMcpClient && IsExternallyManaged("apiBaseUrl") && !string.Equals(Configuration.ApiBaseUrl?.TrimEnd('/'), origin.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new AgentClientValidationException("Instance URL is managed by the environment. Change its source before setup.");
        using var response = await _http.GetAsync(new Uri(origin, "/api/integration-pairing/v1/capabilities"), ct);
        PairingCapabilities? capabilities = null;
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Older instances can be configured explicitly, but no generic health
            // response establishes compatibility or enables browser approval.
            capabilities = null;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            capabilities = await response.Content.ReadFromJsonAsync<PairingCapabilities>(cancellationToken: ct) ?? throw new AgentClientValidationException("Instance capability response is empty.");
            if (capabilities.ProtocolVersion != 1 || ValidateDiscoveredOrigin(capabilities.PublicOrigin) != origin)
                throw new AgentClientValidationException("Instance capabilities are incompatible or advertise a different public origin.");
            ValidateTrustedLink(capabilities.VerificationUri, origin);
            if (!string.IsNullOrWhiteSpace(capabilities.HttpMcpResource)) ValidateResource(capabilities.HttpMcpResource);
            else if (Surface == ConfigurationSurface.HttpMcpClient) throw new AgentClientValidationException("This instance does not advertise HTTP MCP client support.");
        }
        ct.ThrowIfCancellationRequested();
        var canonicalResource = !string.IsNullOrWhiteSpace(capabilities?.HttpMcpResource) ? ValidateResource(capabilities.HttpMcpResource).AbsoluteUri : new Uri(origin, "/mcp").AbsoluteUri;
        if (Surface == ConfigurationSurface.HttpMcpClient && IsExternallyManaged("apiBaseUrl") && !string.Equals(Configuration.ApiBaseUrl?.TrimEnd('/'), canonicalResource.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new AgentClientValidationException("HTTP MCP resource is externally managed and differs from the selected instance. Change its managed source before setup.");
        _origin = origin; Capabilities = capabilities;
        _canonicalResource = canonicalResource;
        Configuration = Configuration with { ApiBaseUrl = origin.AbsoluteUri.TrimEnd('/'), ProfileName = ProfileName };
    }
    private static Uri ValidateResource(string raw)
    {
        if (!McpResourceUri.TryNormalize(raw, out var normalized)) throw new AgentClientValidationException("Instance MCP resource must be a canonical HTTPS URL ending in /mcp.");
        return new Uri(normalized);
    }
    private static void ValidateTrustedLink(string raw, Uri origin)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority) || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/account/integration-credentials" || uri.Query is not "" and not "?tab=credentials" || !string.IsNullOrEmpty(uri.Fragment))
            throw new AgentClientValidationException("The browser approval URL is not a trusted instance credential page.");
    }
    public async Task BeginPairingAsync(CancellationToken ct)
    {
        if (Pairing is not null) return;
        if (IsExternallyManaged("IntegrationCredential") || IsExternallyManaged("IntegrationCredentialFile")) throw new AgentClientValidationException("This credential is externally managed; use the explicit external authentication path instead of browser pairing.");
        if (Capabilities is not { PairingSupported: true } || _origin is null) throw new AgentClientValidationException("Browser pairing is unavailable. Import an existing credential or configure external authentication explicitly.");
        using var response = await _http.PostAsJsonAsync(new Uri(_origin, "/api/integration-pairing/v1/start"), new { name = ProfileName, purpose = Surface == ConfigurationSurface.HttpMcpClient ? 1 : 0, resource = Surface == ConfigurationSurface.HttpMcpClient ? CanonicalTarget : null }, ct);
        response.EnsureSuccessStatusCode();
        var pairing = await response.Content.ReadFromJsonAsync<PairingStart>(cancellationToken: ct) ?? throw new AgentClientValidationException("Empty pairing response.");
        ValidateTrustedLink(pairing.VerificationUri, _origin);
        if (ValidateDiscoveredOrigin(pairing.PublicOrigin) != _origin) throw new AgentClientValidationException("Pairing origin changed.");
        ct.ThrowIfCancellationRequested();
        Pairing = pairing; PairingStatus = "pending";
    }
    public async Task<PairingPoll> PollAsync(CancellationToken ct)
    {
        if (Pairing is null || _origin is null) throw new AgentClientValidationException("Start pairing first.");
        if (DateTimeOffset.UtcNow >= Pairing.ExpiresAtUtc) { PairingStatus = "expired"; return new("expired", 5, null); }
        using var response = await _http.PostAsJsonAsync(new Uri(_origin, "/api/integration-pairing/v1/poll"), new { deviceCapability = Pairing.DeviceCapability }, ct);
        response.EnsureSuccessStatusCode();
        var poll = await response.Content.ReadFromJsonAsync<PairingPoll>(cancellationToken: ct) ?? throw new AgentClientValidationException("Empty pairing status.");
        ct.ThrowIfCancellationRequested();
        PairingStatus = poll.Status;
        if (poll.Status == "issued" && poll.Delivery is { } delivery)
        {
            if (Surface == ConfigurationSurface.HttpMcpClient && !McpResourceUri.Equivalent(delivery.Resource, CanonicalTarget)) throw new AgentClientValidationException("Credential was issued for a different MCP resource.");
            if (delivery.Purpose != (Surface == ConfigurationSurface.HttpMcpClient ? PairingCredentialPurpose.HttpMcp : PairingCredentialPurpose.Api)) throw new AgentClientValidationException("Credential purpose differs from the selected transport.");
            Delivery = delivery;
            UseCredential(delivery.Secret);
        }
        if (Delivery is { } approved && !IsExternallyManaged("DefaultTenantId"))
        {
            var tenants = approved.Grants.Select(grant => grant.TenantId).Distinct().ToArray();
            if (tenants.Length == 1 && Configuration.DefaultTenantId is null) SelectTenant(tenants[0]);
        }
        return poll;
    }
    public void UseCredential(string secret)
    {
        if (_origin is null) throw new AgentClientValidationException("Validate an instance first.");
        if (string.IsNullOrWhiteSpace(secret) || System.Text.Encoding.UTF8.GetByteCount(secret) > 16383 || secret.Any(char.IsControl)) throw new AgentClientValidationException("Enter a valid existing credential.");
        if (IsExternallyManaged("integrationCredential") || IsExternallyManaged("IntegrationCredentialFile")) throw new AgentClientValidationException("Credential is externally managed and is read-only.");
        if (IsExternallyManaged("AuthenticationMode") && !string.Equals(Configuration.AuthenticationMode, "IntegrationCredential", StringComparison.OrdinalIgnoreCase) || new[] { "OidcTokenUrl", "OidcAppPassword", "OidcAppPasswordFile", "ApiM2MTokenUrl", "ApiM2MClientSecret", "ApiM2MClientSecretFile" }.Any(IsExternallyManaged))
            throw new AgentClientValidationException("Authentication is externally managed. Update its source or use the external authentication path.");
        var managedTenant = IsExternallyManaged("DefaultTenantId") ? Configuration.DefaultTenantId : null;
        if (managedTenant is not null && Delivery is not null && (!int.TryParse(managedTenant, out var tenantId) || !Delivery.Grants.Any(grant => grant.TenantId == tenantId)))
            throw new AgentClientValidationException("The externally managed default tenant is outside the approved grants. Update its source before saving.");
        if (managedTenant is null && Delivery is not null && Configuration.IntegrationCredential == secret &&
            int.TryParse(Configuration.DefaultTenantId, out var previousTenant) && Delivery.Grants.Any(grant => grant.TenantId == previousTenant))
            managedTenant = Configuration.DefaultTenantId;
        _reuseExistingConfiguration = false;
        Configuration = AgentClientConfiguration.Empty with { ApiBaseUrl = CanonicalTarget, DefaultTenantId = managedTenant, IntegrationCredential = secret, ProfileName = ProfileName, Instance = Configuration.Instance ?? "prod", AuthenticationMode = "IntegrationCredential", CredentialPurpose = Surface == ConfigurationSurface.HttpMcpClient ? "HttpMcp" : "Api", ApprovedOrigin = new Uri(CanonicalTarget).GetLeftPart(UriPartial.Authority), ApprovedTarget = CanonicalTarget };
    }
    public void SelectTenant(int? tenant)
    {
        if (IsExternallyManaged("DefaultTenantId")) throw new AgentClientValidationException("Default tenant is externally managed and read-only.");
        if (tenant is not null && (Delivery is null || !Delivery.Grants.Any(g => g.TenantId == tenant))) throw new AgentClientValidationException("Default tenant must come from the browser-approved grants.");
        Configuration = Configuration with { DefaultTenantId = tenant?.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }
    public void UseExternalConfiguration()
    {
        var result = _configuration.Load(new(Surface, Path, ProfileName, GetEnvironmentVariable: _environment), allowCreate: true);
        if (!result.Validate().IsValid) throw new AgentClientValidationException("External authentication is incomplete. Supply OIDC or API M2M flags/environment/file configuration, then run config validate.");
        if (!string.Equals(result.Configuration.ApiBaseUrl?.TrimEnd('/'), CanonicalTarget.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new AgentClientValidationException("External authentication is bound to a different instance/resource. Select its configured target or explicitly reauthorize and update its source before setup.");
        Configuration = result.Configuration; _reuseExistingConfiguration = true;
    }
    public async Task<(string TokenEndpoint, string Scope)> DiscoverExternalProviderAsync(CancellationToken ct)
    {
        var raw = Capabilities?.HttpMcpOidcAuthority;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var authority) || authority.Scheme != "https" || authority.UserInfo.Length != 0 || authority.Query.Length != 0 || authority.Fragment.Length != 0)
            throw new AgentClientValidationException("The instance must advertise its existing HTTPS OIDC authority. Correct its provider metadata before retrying.");
        using var response = await _http.GetAsync(raw!.TrimEnd('/') + "/.well-known/openid-configuration", ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty("issuer", out var issuer) || !NetRatelConfigurationService.Equivalent(issuer.GetString(), raw) ||
            !document.RootElement.TryGetProperty("token_endpoint", out var endpoint) || !Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out var token) || token.Scheme != "https" || token.UserInfo.Length != 0 || token.Fragment.Length != 0)
            throw new AgentClientValidationException("The existing OIDC provider did not advertise a matching issuer and trusted HTTPS token endpoint. Review provider metadata before retrying.");
        ct.ThrowIfCancellationRequested();
        var scopes = Capabilities?.HttpMcpRequiredScopes ?? [];
        return (token.AbsoluteUri, scopes.Count == 0 ? AgentClientConfiguration.DefaultScope : string.Join(' ', scopes));
    }
    public void UseGuidedExternalConfiguration(AgentClientConfiguration supplied)
    {
        if (_origin is null) throw new AgentClientValidationException("Connect to an instance first.");
        if (!NetRatelConfigurationService.Equivalent(supplied.ApiBaseUrl, CanonicalTarget))
            throw new AgentClientValidationException("The external authentication target differs from the selected canonical instance resource. Review the instance before authorizing it.");
        foreach (var property in typeof(AgentClientConfiguration).GetProperties().Where(property => property.PropertyType == typeof(string)))
            if (IsExternallyManaged(property.Name) && !Equals(property.GetValue(Configuration), property.GetValue(supplied)))
                throw new AgentClientValidationException(property.Name + " is externally managed. Keep its selected source or change that source explicitly.");
        if (!new AgentClientConfigurationValidator(Surface).Validate(null, supplied).Succeeded)
            throw new AgentClientValidationException("Complete the provider fields shown below using your approved external-provider application account.");
        _reuseExistingConfiguration = false;
        Configuration = supplied;
    }
    public void Review() => _reviewHash = File.Exists(Path) ? ConfigurationFileStorage.Hash(Path) : string.Empty;
    public async Task SaveAsync(bool replace, CancellationToken ct)
    {
        await _saveGate.WaitAsync(ct);
        try
        {
            if (!_saved)
            {
                ct.ThrowIfCancellationRequested();
                if (_reuseExistingConfiguration)
                {
                    var current = _configuration.Load(new(Surface, Path, ProfileName, GetEnvironmentVariable: _environment), allowCreate: true);
                    if (!current.Validate().IsValid || current.Configuration != Configuration || _reviewHash != (File.Exists(Path) ? ConfigurationFileStorage.Hash(Path) : string.Empty))
                        throw new AgentClientValidationException("The selected configuration changed after review. Reload its managed source before verification.");
                }
                else _configuration.SaveProfile(Path, Configuration, Surface, replace: replace, explicitRebind: Delivery is not null, expectedExistingHash: _reviewHash);
                _persistedCredentialFile = File.Exists(Path) ? NetRatelConfigurationService.ReadConfiguration(Path).IntegrationCredentialFile : null;
                Configuration = _configuration.Load(new(Surface, Path, ProfileName, GetEnvironmentVariable: _environment), allowCreate: _reuseExistingConfiguration).Configuration;
                _saved = true;
            }
            if (Pairing is not null && Delivery is not null && _origin is not null && !_acknowledged)
            {
                using var response = await _http.PostAsJsonAsync(new Uri(_origin, "/api/integration-pairing/v1/acknowledge"), new { deviceCapability = Pairing.DeviceCapability, credentialId = Delivery.CredentialId }, ct);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (!document.RootElement.TryGetProperty("status", out var status) || status.GetString() != "consumed")
                    throw new AgentClientValidationException("The saved pairing delivery was not acknowledged. Retry Save while this request is valid, or pair again if it expired.");
                _acknowledged = true;
            }
        }
        finally { _saveGate.Release(); }
    }
    public async Task<IReadOnlyList<SetupCheck>> DoctorAsync(CancellationToken ct, EffectiveAgentClientConfiguration? snapshot = null)
    {
        var loaded = snapshot ?? _configuration.Load(new(Surface, Path, ProfileName, GetEnvironmentVariable: _environment), allowCreate: _reuseExistingConfiguration);
        if (Surface != ConfigurationSurface.HttpMcpClient)
        {
            var report = await new NetRatelDoctorService().CheckAsync(loaded, connectivity: true, ct);
            var results = report.Checks.Select(check => new SetupCheck(check.Name, !check.Completed ? "unavailable" : check.Passed ? "completed" : check.HttpStatus is 401 or 403 ? "unauthorized" : "failed", check.Message, check.HttpStatus)).ToList();
            if (Surface == ConfigurationSurface.StdioMcp)
                results.AddRange(await new StdioMcpDoctor().CheckAsync(loaded, ct));
            if (_origin is not null && Capabilities is null) results.Add(new("instance capabilities", "unavailable", "Pairing is unavailable and instance compatibility was not confirmed; the protected profile was explicitly configured."));
            return results;
        }
        return await CheckHttpMcpAsync(loaded, ct);
    }
    public Task<IReadOnlyList<SetupCheck>> VerifyTransientHttpMcpAsync(CancellationToken ct)
    {
        if (Surface != ConfigurationSurface.HttpMcpClient || !HasConfiguredAuthentication)
            throw new AgentClientValidationException("Configure a separate personal HTTP MCP verification identity first.");
        return CheckHttpMcpAsync(new(Configuration, Surface, null, ProfileName, Sources), ct);
    }
    private async Task<IReadOnlyList<SetupCheck>> CheckHttpMcpAsync(EffectiveAgentClientConfiguration loaded, CancellationToken ct)
    {
        var validation = loaded.Validate();
        var checks = new List<SetupCheck> { new("configuration", validation.IsValid ? "completed" : "failed", validation.IsValid ? "Configuration and credential binding are valid." : "Configuration is incomplete or invalid; run config validate.") };
        if (_origin is not null && Capabilities is null) checks.Add(new("instance capabilities", "unavailable", "Pairing is unavailable and instance compatibility was not confirmed."));
        if (!validation.IsValid) return checks;
        try
        {
            var resolved = loaded.Configuration;
            var token = await GetVerificationTokenAsync(resolved, ct);
            if (Surface == ConfigurationSurface.HttpMcpClient)
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    checks.Add(new("external OAuth", "unavailable", "Validate external OAuth through the separately configured third-party client.")); return checks;
                }
                var target = ValidateResource(resolved.ApiBaseUrl!);
                var initialize = await SendMcpAsync(target, token, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "NetRatel setup", version = typeof(SetupSession).Assembly.GetName().Version!.ToString() } } }, null, ct);
                checks.Add(new("reachability", "completed", "Canonical HTTPS MCP resource responded."));
                if (initialize.Status is 401 or 403) { checks.Add(new("authentication", "unauthorized", $"HTTP {initialize.Status}")); return checks; }
                if (initialize.Status is < 200 or >= 300 || initialize.Result is null || !IsMcpReply(initialize.Result.Value, 1) || !initialize.Result.Value.TryGetProperty("result", out var initialized) || !initialized.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.String || protocol.GetString() is not ("2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25")) { checks.Add(new("MCP initialize", "unavailable", "Resource did not complete the MCP handshake.")); return checks; }
                checks.Add(new("authentication", "completed", "The bound credential was accepted."));
                checks.Add(new("MCP initialize", "completed", "Protocol handshake completed."));
                await SendMcpAsync(target, token, new { jsonrpc = "2.0", method = "notifications/initialized" }, initialize.SessionId, ct);
                var tools = await SendMcpAsync(target, token, new { jsonrpc = "2.0", id = 2, method = "tools/list" }, initialize.SessionId, ct);
                if (tools.Status is < 200 or >= 300 || tools.Result is null || !IsMcpReply(tools.Result.Value, 2) || !tools.Result.Value.TryGetProperty("result", out var catalog) || !catalog.TryGetProperty("tools", out var offered) || offered.ValueKind != JsonValueKind.Array || !offered.EnumerateArray().Any(t => t.TryGetProperty("name", out var name) && name.GetString() == "netratel_auth"))
                { checks.Add(new("permitted read-only tool", "unavailable", "The authentication status tool is unavailable.")); return checks; }
                var read = await SendMcpAsync(target, token, new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "netratel_auth", arguments = new { operation = "status" } } }, initialize.SessionId, ct);
                var successful = read.Status is >= 200 and < 300 && read.Result is { } envelope && IsMcpReply(envelope, 3) && envelope.TryGetProperty("result", out var result) && !(result.TryGetProperty("isError", out var failed) && failed.ValueKind == JsonValueKind.True) && result.TryGetProperty("structuredContent", out var structured) && structured.TryGetProperty("success", out var accepted) && accepted.ValueKind == JsonValueKind.True && structured.TryGetProperty("data", out var authStatus) && StdioMcpDoctor.IsAuthenticationStatus(authStatus);
                checks.Add(new("permitted read-only tool", successful ? "completed" : read.Status is 401 or 403 ? "unauthorized" : "unavailable", successful ? "Authentication status tool completed without mutation." : "Status tool was unavailable or not authorized; no permission was granted."));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AgentClientRemoteException ex)
        { checks.Add(new("external authentication", ex.StatusCode is 401 or 403 ? "unauthorized" : "unavailable", "The selected provider rejected the bounded token request. Check its approved account, scope and token endpoint, then retry.")); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AgentClientValidationException or JsonException)
        { checks.Add(new("transport", "unavailable", "The bounded read-only transport check could not complete.")); }
        return checks;
    }
    private async Task<string> GetVerificationTokenAsync(AgentClientConfiguration configuration, CancellationToken ct)
    {
        // Use the runtime's token contract and the same redirect-disabled transport as
        // discovery/MCP. The wrapper does not own the session's shared handler.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(12));
        return await new NetRatelAgentClient(configuration, () => new VerificationTransport(_transport))
            .GetAccessTokenAsync(bounded.Token);
    }
    private sealed class VerificationTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) { }
    }
    private static bool IsMcpReply(JsonElement value, int expectedId) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("jsonrpc", out var rpc) && rpc.ValueKind == JsonValueKind.String && rpc.GetString() == "2.0" &&
        value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var received) && received == expectedId &&
        !value.TryGetProperty("error", out _) && value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object;
    private async Task<(int Status, string? SessionId, JsonElement? Result)> SendMcpAsync(Uri target, string token, object body, string? sessionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-03-26");
        if (sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var assigned = response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : sessionId;
        if (!response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Accepted) return ((int)response.StatusCode, assigned, null);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct); bound.CancelAfter(TimeSpan.FromSeconds(12));
        using var reader = new StreamReader(stream);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            var size = 0; var line = new System.Text.StringBuilder(); var chunk = new char[4096];
            JsonElement? TakeDataLine()
            {
                var text = line.ToString(); line.Clear();
                if (!text.StartsWith("data:", StringComparison.Ordinal)) return null;
                using var document = JsonDocument.Parse(text[5..].Trim());
                return document.RootElement.Clone();
            }
            while (true)
            {
                var read = await reader.ReadAsync(chunk.AsMemory(), bound.Token);
                if (read == 0) break;
                for (var index = 0; index < read; index++)
                {
                    if (++size > MaximumMcpResponseCharacters) throw new AgentClientValidationException("MCP response exceeds the setup validation bound.");
                    if (chunk[index] != '\n') { line.Append(chunk[index]); continue; }
                    if (TakeDataLine() is { } data) return ((int)response.StatusCode, assigned, data);
                }
            }
            return ((int)response.StatusCode, assigned, TakeDataLine());
        }
        var buffer = new char[MaximumMcpResponseCharacters + 1]; var count = 0;
        while (count < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(count), bound.Token);
            if (read == 0) break;
            count += read;
        }
        if (count == buffer.Length) throw new AgentClientValidationException("MCP response exceeds the setup validation bound.");
        using var json = JsonDocument.Parse(new string(buffer, 0, count));
        return ((int)response.StatusCode, assigned, json.RootElement.Clone());
    }
    public string LaunchSnippet()
    {
        if (Surface == ConfigurationSurface.StdioMcp)
        {
            var launch = StdioMcpDoctor.ResolveLaunch();
            if (launch is null)
                return "Install the stdio MCP archive, then run its NetRatel.Mcp apphost with NETRATEL_MCP_CONFIG=" + ShellArgument.Quote(Path) + ". The stdio executable is not present beside this CLI installation; transport validation remains pending.";
            return JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["netratel"] = new { command = launch.Command, args = launch.Arguments, env = new { NETRATEL_MCP_CONFIG = Path } } } }, new JsonSerializerOptions { WriteIndented = true });
        }
        if (Surface == ConfigurationSurface.HttpMcpClient)
        {
            var target = _origin is null ? Configuration.ApiBaseUrl ?? "" : CanonicalTarget;
            if (NetRatelConfigurationService.AuthenticationModeOf(Configuration) == AgentClientAuthenticationMode.Oidc)
                return JsonSerializer.Serialize(new
                {
                    url = target,
                    authentication = "OAuth sign-in in your MCP client",
                    resourceMetadata = new Uri(new Uri(target), "/.well-known/oauth-protected-resource/mcp").AbsoluteUri,
                    authorizationServer = Capabilities?.HttpMcpOidcAuthority,
                    scopes = (Configuration.OidcScope ?? AgentClientConfiguration.DefaultScope).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    protectedProfile = Path,
                    instruction = "Use the advertised OAuth authorization server. The protected profile retains the verified provider configuration; no bearer token is embedded."
                }, new JsonSerializerOptions { WriteIndented = true });
            return JsonSerializer.Serialize(new { url = target, headers = new { Authorization = "Bearer ${NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN}" }, credentialFile = _persistedCredentialFile ?? Configuration.IntegrationCredentialFile }, new JsonSerializerOptions { WriteIndented = true });
        }
        return "netratel --config " + ShellArgument.Quote(Path) + " auth status";
    }
    public async Task CancelPairingAsync()
    {
        if (_saved) return;
        var pairing = Pairing; Pairing = null;
        if (pairing is null || _origin is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { using var response = await _http.PostAsJsonAsync(new Uri(_origin, "/api/integration-pairing/v1/cancel"), new { deviceCapability = pairing.DeviceCapability }, timeout.Token); response.EnsureSuccessStatusCode(); }
        finally { DiscardUnpersistedAuthentication(); PairingStatus = "cancelled"; }
    }
    public void DiscardUnpersistedAuthentication()
    {
        if (_saved) return;
        Configuration = _initialConfiguration;
        Sources = _initialSources;
        Delivery = null; _reuseExistingConfiguration = false;
    }
    public async ValueTask DisposeAsync() { try { await CancelPairingAsync(); } catch (HttpRequestException) { } catch (TaskCanceledException) { } _http.Dispose(); _saveGate.Dispose(); }
}

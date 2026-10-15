using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetRatel.AgentClient;
using NetRatel.Shared.Connectivity;
using R3;
using Termina.Clipboard;
using Termina.Extensions;
using Termina.Hosting;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;

namespace NetRatel.Setup;

public static class HostingSetupTerminal
{
    public static Task<int> RunAsync(CancellationToken ct) => RunAsync(new Dictionary<string, string>(), ct);
    public static async Task<int> RunAsync(IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
        builder.Logging.ClearProviders();
        var model = new HostingSetupViewModel(options);
        builder.Services.AddTermina("/hosting", t => t.RegisterRoute<HostingSetupPage, HostingSetupViewModel>("/hosting", sp => new HostingSetupPage(sp.GetRequiredService<IClipboardService>()), sp => model));
        using var host = builder.Build();
        try { await host.RunAsync(ct); return model.Verified ? 0 : model.Result is not null ? 2 : 130; }
        finally { await model.CleanupAsync(); }
    }
}
public enum HostingSetupStep { Instance, Authorization, Review, Apply, Verify, Finish }
public sealed class HostingSetupViewModel : ReactiveViewModel
{
    private readonly NetRatelHttpHostingSetupService _service = new();
    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _handler;
    private CancellationTokenSource _operation = new();
    private HttpHostingSetupRequest? _reviewedRequest;
    private HttpHostSetupStartResponse? _approval;
    private string? _fingerprint;
    private string? _approvedFingerprint;
    private SetupSession? _verification;
    private int _generation;
    private bool _disposed;
    private bool _authModeExplicit;
    private readonly IReadOnlyDictionary<string, string> _options;
    private string? _loadedLocation;
    private bool? _operatorSurfaceEnabled;
    private string? _composeOutputDirectory;
    private string? _apiServiceAuthority;
    private string _apiServiceAudience = "netratel.api";
    private bool _hasSavedRequest;
    private bool _m2mCallersKnown;
    public HostingSetupViewModel(IReadOnlyDictionary<string, string>? options = null, HttpMessageHandler? handler = null)
    {
        options ??= new Dictionary<string, string>();
        _options = options;
        _handler = handler;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 262144 };
        OutputDirectory = options.GetValueOrDefault("--output-directory", "");
        PublicOrigin = options.GetValueOrDefault("--instance") ?? options.GetValueOrDefault("--public-origin", "");
        ApiBaseUrl = options.GetValueOrDefault("--api-base-url", "");
        _authModeExplicit = options.ContainsKey("--auth-mode");
        AuthMode = options.GetValueOrDefault("--auth-mode") == "oidc" ? "OIDC" : "Local credential";
        OidcAuthority = options.GetValueOrDefault("--oidc-authority", "");
        RequiredGroups = options.GetValueOrDefault("--required-groups", ""); RequiredScopes = options.GetValueOrDefault("--required-scopes", "");
        Instance = options.GetValueOrDefault("--instance-name", "prod"); Resource = options.GetValueOrDefault("--resource", "");
        Port = options.GetValueOrDefault("--listen-port", "9224"); AllowedOrigins = options.GetValueOrDefault("--allowed-origins", "");
        ExistingOutbound = options.GetValueOrDefault("--existing-outbound-config") ?? Environment.GetEnvironmentVariable("NETRATEL_MCP_CONFIG") ?? Environment.GetEnvironmentVariable("NetRatel__Mcp__Http__ConfigurationPath") ?? "";
        DelegationPrincipal = options.GetValueOrDefault("--delegation-principal", "");
        ExistingDelegationKeyFile = options.GetValueOrDefault("--delegation-key-file") ?? Environment.GetEnvironmentVariable("NETRATEL_MCP_DELEGATION_KEY_FILE") ?? "";
        DelegationKeyId = options.GetValueOrDefault("--delegation-key-id") ?? Environment.GetEnvironmentVariable("NETRATEL_MCP_DELEGATION_KEY_ID") ?? "";
        DelegationIssuer = options.GetValueOrDefault("--delegation-issuer") ?? Environment.GetEnvironmentVariable("NETRATEL_MCP_DELEGATION_ISSUER") ?? "";
        DelegationAudience = options.GetValueOrDefault("--delegation-audience") ?? Environment.GetEnvironmentVariable("NETRATEL_MCP_DELEGATION_AUDIENCE") ?? "";
        ExistingM2MCallers = options.GetValueOrDefault("--existing-m2m-callers", "");
        _m2mCallersKnown = options.ContainsKey("--existing-m2m-callers");
        _apiServiceAudience = options.GetValueOrDefault("--api-m2m-audience", "netratel.api");
    }
    public override void OnActivated()
    {
        Input.OfType<IInputEvent, ResizeEvent>().Subscribe(size => { Width = size.Width; Revision.Value++; }).DisposeWith(Subscriptions);
    }
    public int Width { get; private set; } = 80;
    public void EditInput(Action change) { if (Busy) CancelOperation(); change(); }
    public ReactiveProperty<int> Revision { get; } = new(0);
    public ReactiveProperty<string> Error { get; } = new("");
    public HostingSetupStep Step { get; private set; } = HostingSetupStep.Instance;
    public bool Busy { get; private set; }
    public bool Verified => Step == HostingSetupStep.Finish && Checks.Count > 0 && Checks.All(c => c.Status == "completed");
    public string VerificationTokenUrl { get; private set; } = "";
    public string VerificationClientId { get; set; } = "";
    public string VerificationUsername { get; set; } = "";
    public string VerificationPassword { get; set; } = "";
    public string VerificationScope { get; set; } = "";
    public void SelectAuthMode(string mode) { AuthMode = mode; _authModeExplicit = true; Revision.Value++; }
    public string OutputDirectory { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "";
    public string OidcAuthority { get; set; } = "";
    public string ApiBackend { get; set; } = "http://api:9222/";
    public string McpBackend { get; set; } = "http://mcp-http:9224/";
    public string AuthMode { get; set; } = "Local credential";
    public string Instance { get; set; } = "prod";
    public string RequiredGroups { get; set; } = "";
    public string RequiredScopes { get; set; } = "";
    public string Resource { get; set; } = "";
    public string ExistingOutbound { get; set; } = "";
    public string DelegationPrincipal { get; set; } = "";
    public string ExistingDelegationKeyFile { get; set; } = "";
    public string DelegationKeyId { get; set; } = "";
    public string DelegationIssuer { get; set; } = "";
    public string DelegationAudience { get; set; } = "";
    public string ExistingM2MCallers { get; set; } = "";
    public string AllowedOrigins { get; set; } = "";
    public string Port { get; set; } = "9224";
    public bool Advanced { get; set; }
    public bool ConfirmReplace { get; private set; }
    public HttpHostingSetupPreview? Preview { get; private set; }
    public HttpHostingSetupResult? Result { get; private set; }
    public IReadOnlyList<SetupCheck> Checks { get; private set; } = [];
    public string? VerificationUrl => Step == HostingSetupStep.Verify ? _verification?.Pairing?.VerificationUri : _approval?.VerificationUri;
    public string? RegistrationFingerprint => _fingerprint;
    public string? UserCode => Step == HostingSetupStep.Verify ? _verification?.Pairing?.UserCode : _approval?.UserCode;
    public string ApplyCommand => _reviewedRequest is null ? "" : NetRatelHttpHostingSetupService.ApplyCommand(_reviewedRequest);
    private static string[] Values(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private HttpHostingSetupRequest Request() => new(OutputDirectory, PublicOrigin, ApiBaseUrl, Instance, AuthMode == "Local credential")
    {
        OidcAuthority = string.IsNullOrWhiteSpace(OidcAuthority) ? null : OidcAuthority,
        RequiredGroups = Values(RequiredGroups), RequiredScopes = Values(RequiredScopes),
        WebApiBackendUrl = ApiBackend, WebMcpBackendUrl = McpBackend,
        PublicResourceUri = string.IsNullOrWhiteSpace(Resource) ? null : Resource,
        ExistingOutboundConfigurationPath = string.IsNullOrWhiteSpace(ExistingOutbound) ? null : ExistingOutbound,
        DelegationServicePrincipal = string.IsNullOrWhiteSpace(DelegationPrincipal) ? null : DelegationPrincipal,
        ExistingDelegationKeyFile = string.IsNullOrWhiteSpace(ExistingDelegationKeyFile) ? null : ExistingDelegationKeyFile,
        DelegationKeyId = string.IsNullOrWhiteSpace(DelegationKeyId) ? null : DelegationKeyId,
        DelegationIssuer = string.IsNullOrWhiteSpace(DelegationIssuer) ? null : DelegationIssuer,
        DelegationAudience = string.IsNullOrWhiteSpace(DelegationAudience) ? null : DelegationAudience,
        ExistingM2MCallerClientIds = _m2mCallersKnown || !string.IsNullOrWhiteSpace(ExistingM2MCallers) ? Values(ExistingM2MCallers) : null, AllowedOrigins = string.IsNullOrWhiteSpace(AllowedOrigins) ? null : Values(AllowedOrigins),
        ListeningPort = int.TryParse(Port, out var port) ? port : throw new AgentClientValidationException("Listening port must be an integer."),
        ComposeOutputDirectory = Environment.GetEnvironmentVariable("NETRATEL_SETUP_HOST_OUTPUT_DIRECTORY") ?? _composeOutputDirectory,
        OperatorSurfaceEnabled = _operatorSurfaceEnabled,
        ApprovedRegistrationFingerprint = _approvedFingerprint,
        ApiServiceAudience = _apiServiceAudience, ApiServiceAuthority = _apiServiceAuthority
    };
    public void Next(bool replace = false) => _ = NextAsync(replace);
    public async Task NextAsync(bool replace = false)
    {
        if (Busy || _disposed) return;
        var generation = _generation; var ct = _operation.Token;
        Busy = true; Error.Value = "Working…"; Revision.Value++;
        try
        {
            switch (Step)
            {
                case HostingSetupStep.Instance:
                    var address = InstanceSetupAddress.Parse(PublicOrigin);
                    OutputDirectory = HttpHostingStorage.Resolve(OutputDirectory); HttpHostingStorage.ValidateWritable(OutputDirectory);
                    AdoptSavedRequest(address);
                    using (var response = await _http.GetAsync(address.Origin + HttpHostSetupProtocol.Route + "/capabilities", ct))
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound) throw new AgentClientValidationException("This instance needs a compatible HTTP host setup release. Upgrade the instance, then retry this URL.");
                        if (!response.IsSuccessStatusCode) throw new AgentClientValidationException("Instance discovery failed. Check the Web URL and HTTPS access, then retry.");
                        var capabilities = await response.Content.ReadFromJsonAsync<HttpHostSetupCapabilities>(cancellationToken: ct) ?? throw new AgentClientValidationException("Instance discovery returned no setup metadata.");
                        if (capabilities.ProtocolVersion != HttpHostSetupProtocol.Version || !NetRatelConfigurationService.Equivalent(capabilities.PublicOrigin, address.Origin))
                            throw new AgentClientValidationException("Instance setup metadata is incompatible or belongs to another origin.");
                        ValidateApprovalLink(capabilities.VerificationUri, address.Origin);
                        var localGeneration = File.Exists(Path.Combine(OutputDirectory, "secrets", "delegation.key")) && File.Exists(Path.Combine(OutputDirectory, "secrets", "outbound-m2m.secret"));
                        var adoptedGeneration = ExistingOutbound.Length > 0 && ExistingDelegationKeyFile.Length > 0;
                        if (capabilities.ExistingHostConfigured && !localGeneration && !adoptedGeneration)
                        { Advanced = true; throw new AgentClientValidationException("This instance already has an HTTP host identity. Reuse its existing setup volume, or select its existing protected outbound configuration and delegation key in Advanced. Setup will not rotate or replace that identity."); }
                        if (!McpResourceUri.TryNormalize(capabilities.Resource, out var resource)) throw new AgentClientValidationException("The instance advertises an invalid canonical MCP resource.");
                        ct.ThrowIfCancellationRequested();
                        PublicOrigin = address.Origin;
                        if (ApiBaseUrl.Length == 0) ApiBaseUrl = address.Origin;
                        if (Resource.Length == 0) Resource = resource;
                        if (!capabilities.AuthenticationModeKnown && !_authModeExplicit)
                        { Advanced = true; throw new AgentClientValidationException("This existing deployment does not advertise its ingress authentication mode. In Advanced select its current Local credential or OIDC mode, then retry; no mode was changed."); }
                        if (!_authModeExplicit) AuthMode = capabilities.LocalCredentialMode ? "Local credential" : "OIDC";
                        if (OidcAuthority.Length == 0) OidcAuthority = capabilities.OidcAuthority ?? "";
                        if (RequiredGroups.Length == 0) RequiredGroups = string.Join(',', capabilities.RequiredGroups);
                        if (RequiredScopes.Length == 0) RequiredScopes = string.Join(',', capabilities.RequiredScopes);
                        if (!_hasSavedRequest && !_options.ContainsKey("--api-m2m-audience")) _apiServiceAudience = capabilities.ApiServiceAudience;
                        if (!_hasSavedRequest) _apiServiceAuthority = capabilities.ApiServiceAuthority;
                        if (!_options.ContainsKey("--existing-m2m-callers")) ExistingM2MCallers = string.Join(',', Values(ExistingM2MCallers).Concat(capabilities.ExistingM2MCallerClientIds).Distinct(StringComparer.Ordinal));
                        _m2mCallersKnown = true;
                    }
                    _fingerprint = _service.PrepareRegistration(Request());
                    await SetStepAsync(HostingSetupStep.Authorization, ct);
                    await BeginApprovalAsync(ct);
                    await PollApprovalAsync(ct);
                    break;
                case HostingSetupStep.Authorization:
                    if (_approval is null) await BeginApprovalAsync(ct);
                    await PollApprovalAsync(ct);
                    break;
                case HostingSetupStep.Review:
                    if (Preview!.Changes.Any(change => change.StartsWith("Replace ", StringComparison.Ordinal)) && !replace)
                    { ConfirmReplace = true; break; }
                    if (!string.Equals(_approvedFingerprint, _service.PrepareRegistration(Request()), StringComparison.OrdinalIgnoreCase))
                        throw new AgentClientValidationException("The host identity changed since approval. Go Back and request approval for this generation.");
                    Result = _service.Apply(_reviewedRequest!, replace); ConfirmReplace = false;
                    await SetStepAsync(HostingSetupStep.Apply, ct);
                    break;
                case HostingSetupStep.Apply:
                    await SetStepAsync(HostingSetupStep.Verify, ct);
                    await VerifyAsync(ct);
                    break;
                case HostingSetupStep.Verify:
                    await VerifyAsync(ct);
                    break;
                case HostingSetupStep.Finish:
                    RequestShutdown(); break;
            }
            if (!_disposed && generation == _generation && Error.Value == "Working…") Error.Value = "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_disposed && generation == _generation)
                Error.Value = exception is AgentClientValidationException ? TerminalText.Safe(exception.Message) : "The bounded setup check failed. Check HTTPS connectivity, apply the displayed deployment command, then retry. No readiness result was assumed.";
        }
        finally { if (!_disposed && generation == _generation) { Busy = false; Revision.Value++; } }
    }
    private void AdoptSavedRequest(InstanceSetupAddress address)
    {
        var location = OutputDirectory + "|" + address.Origin;
        if (_loadedLocation == location) return;
        var saved = NetRatelHttpHostingSetupService.LoadRequest(OutputDirectory, address.Origin);
        _loadedLocation = location;
        _hasSavedRequest = saved is not null;
        _m2mCallersKnown |= saved?.ExistingM2MCallerClientIds is not null;
        if (saved is null) return;
        if (!_options.ContainsKey("--api-m2m-audience")) _apiServiceAudience = saved.ApiServiceAudience;
        _apiServiceAuthority = saved.ApiServiceAuthority;
        _operatorSurfaceEnabled = saved.OperatorSurfaceEnabled; _composeOutputDirectory = saved.ComposeOutputDirectory;
        if (ApiBaseUrl.Length == 0) ApiBaseUrl = saved.ApiBaseUrl;
        if (Resource.Length == 0) Resource = saved.PublicResourceUri ?? "";
        if (!_authModeExplicit) { AuthMode = saved.LocalCredentialMode ? "Local credential" : "OIDC"; _authModeExplicit = true; }
        if (OidcAuthority.Length == 0) OidcAuthority = saved.OidcAuthority ?? "";
        if (RequiredGroups.Length == 0) RequiredGroups = string.Join(',', saved.RequiredGroups ?? []);
        if (RequiredScopes.Length == 0) RequiredScopes = string.Join(',', saved.RequiredScopes ?? []);
        if (!_options.ContainsKey("--instance-name")) Instance = saved.Instance;
        if (!_options.ContainsKey("--listen-port")) Port = saved.ListeningPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (AllowedOrigins.Length == 0) AllowedOrigins = string.Join(',', saved.AllowedOrigins ?? []);
        if (ExistingOutbound.Length == 0) ExistingOutbound = saved.ExistingOutboundConfigurationPath ?? "";
        if (DelegationPrincipal.Length == 0) DelegationPrincipal = saved.DelegationServicePrincipal ?? "";
        if (ExistingDelegationKeyFile.Length == 0) ExistingDelegationKeyFile = saved.ExistingDelegationKeyFile ?? "";
        if (DelegationKeyId.Length == 0) DelegationKeyId = saved.DelegationKeyId ?? "";
        if (DelegationIssuer.Length == 0) DelegationIssuer = saved.DelegationIssuer ?? "";
        if (DelegationAudience.Length == 0) DelegationAudience = saved.DelegationAudience ?? "";
        if (ExistingM2MCallers.Length == 0) ExistingM2MCallers = string.Join(',', saved.ExistingM2MCallerClientIds ?? []);
        if (ApiBackend == "http://api:9222/") ApiBackend = saved.WebApiBackendUrl;
        if (McpBackend == "http://mcp-http:9224/") McpBackend = saved.WebMcpBackendUrl;
    }
    private async Task BeginApprovalAsync(CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(PublicOrigin + HttpHostSetupProtocol.Route + "/start", new HttpHostSetupStartRequest(PublicOrigin, Resource, _fingerprint!, string.IsNullOrWhiteSpace(DelegationPrincipal) ? "netratel-mcp-http" : DelegationPrincipal), ct);
        response.EnsureSuccessStatusCode();
        var approval = await response.Content.ReadFromJsonAsync<HttpHostSetupStartResponse>(cancellationToken: ct) ?? throw new AgentClientValidationException("Administrator approval could not start.");
        ValidateApprovalLink(approval.VerificationUri, PublicOrigin);
        if (!NetRatelConfigurationService.Equivalent(approval.PublicOrigin, PublicOrigin)) throw new AgentClientValidationException("Administrator approval belongs to a different origin.");
        ct.ThrowIfCancellationRequested(); _approval = approval; await InvokeAsync(() => Revision.Value++, ct);
    }
    private async Task PollApprovalAsync(CancellationToken ct)
    {
        while (_approval is { } approval && DateTimeOffset.UtcNow < approval.ExpiresAtUtc)
        {
            using var response = await _http.PostAsJsonAsync(PublicOrigin + HttpHostSetupProtocol.Route + "/poll", new HttpHostSetupDeviceRequest(approval.DeviceCapability), ct);
            response.EnsureSuccessStatusCode();
            var poll = await response.Content.ReadFromJsonAsync<HttpHostSetupPollResponse>(cancellationToken: ct) ?? throw new AgentClientValidationException("Administrator approval status is unavailable.");
            ct.ThrowIfCancellationRequested();
            if (poll.Status == "approved")
            {
                if (string.IsNullOrWhiteSpace(poll.ApprovalId) || !string.Equals(poll.RegistrationFingerprint, _fingerprint, StringComparison.OrdinalIgnoreCase)) throw new AgentClientValidationException("Approval does not match this host identity generation. Request approval again.");
                _approvedFingerprint = _fingerprint; _reviewedRequest = Request(); Preview = _service.Preview(_reviewedRequest);
                await SetStepAsync(HostingSetupStep.Review, ct); return;
            }
            if (poll.Status is not ("pending" or "slow_down")) { _approval = null; throw new AgentClientValidationException("Administrator approval " + TerminalText.Safe(poll.Status) + ". Choose Next to request a fresh code."); }
            await InvokeAsync(() => { Error.Value = "Waiting for deployment administrator approval. Open or copy the displayed link and enter the code."; Revision.Value++; }, ct);
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(poll.IntervalSeconds, 5, 30)), ct);
        }
        _approval = null; throw new AgentClientValidationException("Administrator approval expired. Choose Next to request a fresh code.");
    }
    private async Task VerifyAsync(CancellationToken ct)
    {
        _verification ??= new SetupSession(ConfigurationSurface.HttpMcpClient, Path.Combine(OutputDirectory, ".verification-" + Guid.NewGuid().ToString("N") + ".json"), "host-verification", _handler);
        if (AuthMode == "OIDC")
        {
            await _verification.DiscoverAsync(PublicOrigin, ct);
            if (!McpResourceUri.Equivalent(_verification.CanonicalTarget, Resource)) throw new AgentClientValidationException("The applied instance advertises a different MCP resource. Review the deployment and retry.");
            if (VerificationTokenUrl.Length == 0)
            {
                if (!Uri.TryCreate(OidcAuthority, UriKind.Absolute, out var authority) || authority.Scheme != "https" || authority.UserInfo.Length != 0 || authority.Query.Length != 0 || authority.Fragment.Length != 0)
                    throw new AgentClientValidationException("The instance must advertise its existing HTTPS OIDC authority. Keep the configured OIDC deployment and correct its metadata.");
                using var metadata = await _http.GetAsync(OidcAuthority.TrimEnd('/') + "/.well-known/openid-configuration", ct);
                metadata.EnsureSuccessStatusCode();
                using var document = System.Text.Json.JsonDocument.Parse(await metadata.Content.ReadAsStringAsync(ct));
                if (!document.RootElement.TryGetProperty("issuer", out var issuer) || !NetRatelConfigurationService.Equivalent(issuer.GetString(), OidcAuthority) || !document.RootElement.TryGetProperty("token_endpoint", out var endpoint) || !Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out var token) || token.Scheme != "https" || token.UserInfo.Length != 0 || token.Query.Length != 0 || token.Fragment.Length != 0 || !NetRatelConfigurationService.Equivalent(token.GetLeftPart(UriPartial.Authority), authority.GetLeftPart(UriPartial.Authority)))
                    throw new AgentClientValidationException("The configured OIDC provider did not advertise a trusted same-origin token endpoint. Review provider metadata before retrying.");
                ct.ThrowIfCancellationRequested(); VerificationTokenUrl = token.AbsoluteUri; VerificationScope = RequiredScopes.Length == 0 ? AgentClientConfiguration.DefaultScope : string.Join(' ', Values(RequiredScopes));
                await InvokeAsync(() => Revision.Value++, ct);
            }
            if (VerificationClientId.Length == 0 || VerificationUsername.Length == 0 || VerificationPassword.Length == 0)
                throw new AgentClientValidationException("Enter the personal external-provider application account below, then choose Next. The existing host service identity is unchanged.");
            _verification.UseGuidedExternalConfiguration(AgentClientConfiguration.Empty with
            {
                ApiBaseUrl = Resource, AuthenticationMode = "Oidc", OidcTokenUrl = VerificationTokenUrl,
                OidcClientId = VerificationClientId, OidcUsername = VerificationUsername, OidcAppPassword = VerificationPassword, OidcScope = VerificationScope, Instance = Instance
            });
            var externalChecks = await _verification.VerifyTransientHttpMcpAsync(ct);
            ct.ThrowIfCancellationRequested(); Checks = externalChecks;
            if (Checks.Count > 0 && Checks.All(check => check.Status == "completed"))
            { VerificationPassword = ""; await SetStepAsync(HostingSetupStep.Finish, ct); }
            else Error.Value = "External-provider verification is incomplete. Review the checks, current scopes and deployment apply state, then retry.";
            return;
        }
        if (_verification.Pairing is null)
        {
            await _verification.DiscoverAsync(PublicOrigin, ct);
            if (!McpResourceUri.Equivalent(_verification.CanonicalTarget, Resource)) throw new AgentClientValidationException("The applied instance advertises a different MCP resource. Review the deployment and retry.");
            await _verification.BeginPairingAsync(ct); await InvokeAsync(() => Revision.Value++, ct);
        }
        while (_verification.Pairing is { } pairing && DateTimeOffset.UtcNow < pairing.ExpiresAtUtc)
        {
            var poll = await _verification.PollAsync(ct);
            if (poll.Status == "issued")
            {
                var checks = await _verification.VerifyTransientHttpMcpAsync(ct);
                ct.ThrowIfCancellationRequested(); Checks = checks;
                if (checks.Count > 0 && checks.All(check => check.Status == "completed"))
                {
                    await _verification.CancelPairingAsync();
                    await SetStepAsync(HostingSetupStep.Finish, ct);
                }
                else await InvokeAsync(() => { Error.Value = "Deployment verification is incomplete. Review the check result, apply the deployment command if pending, then choose Next to retry."; Revision.Value++; }, ct);
                return;
            }
            if (poll.Status is not ("pending" or "slow_down"))
            {
                await _verification.CancelPairingAsync();
                throw new AgentClientValidationException("Personal verification approval " + TerminalText.Safe(poll.Status) + ". Choose Next to retry.");
            }
            await InvokeAsync(() => { Error.Value = "Approve a separate personal HTTP MCP credential for this read-only check. It is kept in memory and revoked after verification."; Revision.Value++; }, ct);
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(poll.IntervalSeconds, 5, 30)), ct);
        }
        await _verification.CancelPairingAsync(); throw new AgentClientValidationException("Personal verification approval expired. Choose Next to retry.");
    }
    private static void ValidateApprovalLink(string raw, string origin)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var link) || !NetRatelConfigurationService.Equivalent(link.GetLeftPart(UriPartial.Authority), origin) || link.Scheme != "https" || link.UserInfo.Length != 0 || link.Fragment.Length != 0 || link.AbsolutePath != "/account/integration-credentials" || link.Query is not ("" or "?tab=credentials"))
            throw new AgentClientValidationException("The instance supplied an untrusted approval URL. Check the selected Web URL.");
    }
    private Task SetStepAsync(HostingSetupStep step, CancellationToken ct) => InvokeAsync(() => { Step = step; Error.Value = ""; Revision.Value++; }, ct);
    public void ToggleAdvanced() { Advanced = !Advanced; Revision.Value++; }
    public void OpenBrowser()
    {
        if (VerificationUrl is not { } link) return;
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
        catch { Error.Value = "Browser unavailable. Copy the link and enter its code on another device."; }
    }
    public void Back()
    {
        CancelOperation(); ConfirmReplace = false;
        if (Result is not null) Step = HostingSetupStep.Apply;
        else { Step = HostingSetupStep.Instance; Preview = null; _reviewedRequest = null; _approvedFingerprint = null; _ = CancelApprovalAsync(); }
        Error.Value = ""; Revision.Value++;
    }
    private void CancelOperation() { _generation++; _operation.Cancel(); _operation.Dispose(); _operation = new(); Busy = false; }
    public override void RequestShutdown() { CancelOperation(); base.RequestShutdown(); }
    private async Task CancelApprovalAsync()
    {
        var approval = _approval; _approval = null;
        if (approval is null) return;
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { using var response = await _http.PostAsJsonAsync(approval.PublicOrigin.TrimEnd('/') + HttpHostSetupProtocol.Route + "/cancel", new HttpHostSetupDeviceRequest(approval.DeviceCapability), bounded.Token); }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
    }
    public async Task CleanupAsync()
    {
        await CancelApprovalAsync();
        if (_verification is not null) await _verification.DisposeAsync();
        _http.Dispose();
    }
    public override void Dispose() { _disposed = true; VerificationPassword = ""; _operation.Cancel(); _operation.Dispose(); Revision.Dispose(); Error.Dispose(); base.Dispose(); }
}
public sealed class HostingSetupPage(IClipboardService clipboard) : ReactivePage<HostingSetupViewModel>
{
    private readonly List<IFocusable> _controls = [];
    private KeyboardScroll? _scroll;
    private HostingSetupStep _previousStep;
    private int _layoutGeneration;
    private readonly CompositeDisposable _contentSubscriptions = new();
    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();
        ViewModel.Revision.Skip(1).Subscribe(_ => InvalidateLayout()).DisposeWith(Subscriptions);
        KeyBindings.Register(ConsoleKey.F2, () => ViewModel.Next());
        KeyBindings.Register(ConsoleKey.Enter, () => { if (ViewModel.ConfirmReplace) ViewModel.Next(true); else if (Focus.CurrentFocus is SelectionListNode<string> choice) choice.HandleInput(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)); else if (Focus.CurrentFocus is CopyableTextNode copy) copy.TryCopy(); else ViewModel.Next(); });
        KeyBindings.Register(ConsoleKey.LeftArrow, ConsoleModifiers.Alt, ViewModel.Back);
        KeyBindings.Register(ConsoleKey.F4, ViewModel.OpenBrowser);
        KeyBindings.Register(ConsoleKey.F6, ViewModel.ToggleAdvanced);
        KeyBindings.Register(ConsoleKey.Tab, () => CycleFocus(false));
        KeyBindings.Register(ConsoleKey.Tab, ConsoleModifiers.Shift, () => CycleFocus(true));
        KeyBindings.Register(ConsoleKey.PageDown, () => _scroll?.Page(true));
        KeyBindings.Register(ConsoleKey.PageUp, () => _scroll?.Page(false));
        KeyBindings.Register(ConsoleKey.Home, ConsoleModifiers.Control, () => _scroll?.Home(false));
        KeyBindings.Register(ConsoleKey.End, ConsoleModifiers.Control, () => _scroll?.Home(true));
        KeyBindings.Register(ConsoleKey.Escape, () => { if (ViewModel.ConfirmReplace) ViewModel.Back(); else ViewModel.RequestShutdown(); });
        KeyBindings.Register(ConsoleKey.C, ConsoleModifiers.Control, ViewModel.RequestShutdown);
    }
    public override ILayoutNode BuildLayout()
    {
        var generation = ++_layoutGeneration;
        var sameStep = _previousStep == ViewModel.Step; _previousStep = ViewModel.Step;
        var previousFocus = sameStep ? Math.Max(0, _controls.IndexOf(Focus.CurrentFocus!)) : 0;
        var previousOffset = sameStep ? _scroll?.Offset ?? 0 : 0;
        _controls.Clear(); _contentSubscriptions.Clear();
        var form = Layouts.Vertical();
        switch (ViewModel.Step)
        {
            case HostingSetupStep.Instance:
                form.WithChild(Input("Instance Web URL", ViewModel.PublicOrigin, value => ViewModel.PublicOrigin = value));
                form.WithChild(new TextNode("Paste the Web URL, with or without /mcp, then press Enter.").Height(2));
                if (ViewModel.Advanced) AddAdvanced(form);
                break;
            case HostingSetupStep.Authorization:
                form.WithChild(new TextNode("A deployment administrator must approve this dedicated service identity. Personal client credentials remain separate.").Height(3));
                form.WithChild(new TextNode("Registration fingerprint: " + ViewModel.RegistrationFingerprint).Height(2));
                AddApproval(form);
                break;
            case HostingSetupStep.Review:
                form.WithChild(new TextNode("Review the actual deployment changes before saving. Existing identities are reused.").Height(2));
                form.WithChild(new TextNode($"Web: {ViewModel.PublicOrigin}\nMCP: {ViewModel.Resource}\nAuthentication: {ViewModel.AuthMode}\nPersistent storage: {ViewModel.OutputDirectory}").Height(5));
                foreach (var change in ViewModel.Preview!.Changes) form.WithChild(new TextNode(TerminalText.Safe(change)).HeightAuto());
                break;
            case HostingSetupStep.Apply:
                form.WithChild(new TextNode("Configuration written · deployment apply pending\nRun this exact command on the deployment host, then choose Verify. Server processes are not reconfigured by writing files.").Height(4));
                AddCopy(form, ViewModel.ApplyCommand, "Enter copies the reviewed apply command");
                form.WithChild(new TextNode("Persistent storage: " + ViewModel.OutputDirectory).Height(2));
                break;
            case HostingSetupStep.Verify:
                form.WithChild(new TextNode("Verify the applied canonical MCP route and API-backed read-only status operation using a separate personal approval. This credential is never written to host storage.").Height(4));
                if (ViewModel.AuthMode == "OIDC")
                {
                    form.WithChild(new TextNode("Existing OIDC provider: " + ViewModel.OidcAuthority + "\nUse your personal application account for the read-only check.").Height(3));
                    form.WithChild(Input("Provider client ID", ViewModel.VerificationClientId, value => ViewModel.VerificationClientId = value));
                    form.WithChild(Input("Personal application account", ViewModel.VerificationUsername, value => ViewModel.VerificationUsername = value));
                    form.WithChild(Input("Application password", ViewModel.VerificationPassword, value => ViewModel.VerificationPassword = value, password: true));
                    form.WithChild(Input("Required provider scopes", ViewModel.VerificationScope, value => ViewModel.VerificationScope = value));
                }
                else AddApproval(form);
                foreach (var check in ViewModel.Checks) form.WithChild(new TextNode($"{check.Name}: {check.Status} · {check.Detail}").Height(2));
                break;
            case HostingSetupStep.Finish:
                form.WithChild(new TextNode("End-to-end verified · dedicated host identity retained").Bold().Height(2));
                foreach (var check in ViewModel.Checks) form.WithChild(new TextNode($"{check.Name}: {check.Status} · {check.Detail}").Height(2));
                form.WithChild(new TextNode("Persistent storage: " + ViewModel.OutputDirectory + "\nRerun setup with the same URL to reuse this generation.").Height(3));
                break;
        }
        var choices = ViewModel.VerificationUrl is not null && ViewModel.Step is HostingSetupStep.Authorization or HostingSetupStep.Verify
            ? new[] { "Open browser", "Next / retry", "Back", "Cancel" }
            : ViewModel.Step == HostingSetupStep.Instance ? new[] { "Next", "Advanced", "Cancel" } : new[] { ViewModel.Step == HostingSetupStep.Apply ? "Verify after apply" : ViewModel.Step == HostingSetupStep.Finish ? "Finish" : "Next", "Back", "Cancel" };
        var actions = new SelectionListNode<string>(choices, value => "[ " + value + " ]").WithVisibleRows(choices.Length);
        actions.SelectionConfirmed.Subscribe(items => { var selected = items.First(); if (selected == "Advanced") ViewModel.ToggleAdvanced(); else if (selected == "Open browser") ViewModel.OpenBrowser(); else if (selected == "Back") ViewModel.Back(); else if (selected == "Cancel") ViewModel.RequestShutdown(); else ViewModel.Next(); }).DisposeWith(_contentSubscriptions);
        _controls.Add(actions); form.WithChild(actions);
        var scroller = new ScrollableContainerNode().WithContent(form).WithAutoScroll(AutoScrollPolicy.None);
        var formWidth = Math.Max(20, ViewModel.Width - 5);
        _scroll = new KeyboardScroll(scroller, form, formWidth);
        var root = Layouts.Vertical().WithChild(new TextNode("NetRatel · Set up HTTP MCP").Bold().Height(1))
            .WithChild(new PanelNode().WithTitle(ViewModel.Step.ToString()).WithContent(scroller).Fill())
            .WithChild(ViewModel.Error.Select<string, ILayoutNode>(error => new TextNode(error)).AsLayout(RenderFrameProvider).Height(3))
            .WithChild(new TextNode("Enter Next  Alt+Left Back  Esc Cancel  Tab Focus  PgUp/Dn Scroll").NoWrap().Height(1));
        if (ViewModel.ConfirmReplace)
        {
            var modal = new ModalNode().WithTitle("Replace reviewed hosting configuration?").WithContent(new TextNode("Only the displayed configuration replacements are applied. Existing identity secrets are reused.")).WithFooter("Enter Replace · Escape Back"); _controls.Add(modal); Focus.SetFocus(modal); return new StackLayout([root, modal]);
        }
        scroller.Measure(new Size(formWidth, 17)); _scroll.Restore(previousOffset);
        if (_controls.Count > 0) { var selected = _controls[Math.Min(previousFocus, _controls.Count - 1)]; Post(() => { if (generation == _layoutGeneration) { Focus.SetFocus(selected); _scroll?.EnsureVisible(selected); } }); } else Focus.ClearFocus();
        return root;
    }
    private void AddApproval(VerticalLayout form)
    {
        if (ViewModel.VerificationUrl is not { } link) { form.WithChild(new TextNode("Choose Next to request approval. Denied or expired requests can be retried.").Height(2)); return; }
        AddCopy(form, link, "Enter copies link; choose Open browser or use another device");
        AddCopy(form, ViewModel.UserCode ?? "", "Enter copies the approval code");
    }
    private void AddCopy(VerticalLayout form, string text, string hint)
    {
        var copy = new CopyableTextNode(clipboard, TerminalText.Safe(text)).WithHint(hint); _controls.Add(copy); form.WithChild(copy.HeightAuto());
    }
    private void AddAdvanced(VerticalLayout form)
    {
        var auth = new SelectionListNode<string>(["Local credential", "OIDC"], value => value).WithVisibleRows(2).WithHighlightedIndex(ViewModel.AuthMode == "OIDC" ? 1 : 0);
        auth.SelectionConfirmed.Subscribe(items => ViewModel.SelectAuthMode(items.First())).DisposeWith(_contentSubscriptions); _controls.Add(auth); form.WithChild(auth);
        form.WithChild(Input("Persistent output directory (blank uses configured durable default)", ViewModel.OutputDirectory, value => ViewModel.OutputDirectory = value));
        form.WithChild(Input("Separate API HTTPS origin (optional)", ViewModel.ApiBaseUrl, value => ViewModel.ApiBaseUrl = value));
        form.WithChild(Input("Canonical MCP resource (optional)", ViewModel.Resource, value => ViewModel.Resource = value));
        form.WithChild(Input("Instance (dev or prod)", ViewModel.Instance, value => ViewModel.Instance = value));
        form.WithChild(Input("Listening port", ViewModel.Port, value => ViewModel.Port = value));
        form.WithChild(Input("Allowed browser origins (comma-separated)", ViewModel.AllowedOrigins, value => ViewModel.AllowedOrigins = value));
        form.WithChild(Input("Existing outbound protected JSON (optional)", ViewModel.ExistingOutbound, value => ViewModel.ExistingOutbound = value));
        form.WithChild(Input("OIDC authority (discovered when configured)", ViewModel.OidcAuthority, value => ViewModel.OidcAuthority = value));
        form.WithChild(Input("Required groups (comma-separated)", ViewModel.RequiredGroups, value => ViewModel.RequiredGroups = value));
        form.WithChild(Input("Required scopes (comma-separated)", ViewModel.RequiredScopes, value => ViewModel.RequiredScopes = value));
        form.WithChild(Input("Existing delegation principal (optional)", ViewModel.DelegationPrincipal, value => ViewModel.DelegationPrincipal = value));
        form.WithChild(Input("Existing delegation key file (optional)", ViewModel.ExistingDelegationKeyFile, value => ViewModel.ExistingDelegationKeyFile = value));
        form.WithChild(Input("Existing delegation key ID", ViewModel.DelegationKeyId, value => ViewModel.DelegationKeyId = value));
        form.WithChild(Input("Existing delegation issuer", ViewModel.DelegationIssuer, value => ViewModel.DelegationIssuer = value));
        form.WithChild(Input("Existing delegation audience", ViewModel.DelegationAudience, value => ViewModel.DelegationAudience = value));
        form.WithChild(Input("Existing API M2M caller IDs (comma-separated)", ViewModel.ExistingM2MCallers, value => ViewModel.ExistingM2MCallers = value));
        form.WithChild(Input("Web API backend URL", ViewModel.ApiBackend, value => ViewModel.ApiBackend = value));
        form.WithChild(Input("Web MCP backend URL", ViewModel.McpBackend, value => ViewModel.McpBackend = value));
    }
    private void CycleFocus(bool reverse) { Focus.CycleFocus(_controls, reverse); _scroll?.EnsureVisible(Focus.CurrentFocus); }
    private ILayoutNode Input(string label, string value, Action<string> change, bool password = false)
    {
        var node = new TextInputNode { Text = value, MaxLength = 2048, IsPassword = password }; _controls.Add(node);
        node.TextChanged.Subscribe(value => ViewModel.EditInput(() => change(value))).DisposeWith(_contentSubscriptions);
        return Layouts.Vertical().WithChild(new TextNode(label).Height(1)).WithChild(node.Height(1)).Height(2);
    }
    public override void Dispose() { _layoutGeneration++; _contentSubscriptions.Dispose(); base.Dispose(); }
}

using System.Security.Cryptography;
using System.Text.Json;
using NetRatel.AgentClient;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class HttpHostSettingsAdoptionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netratel-host-adoption-" + Guid.NewGuid().ToString("N"));
    private string Output => Path.Combine(_directory, "setup-output");
    private const string Api = "https://api.example.invalid";
    private const string Principal = "existing-deployment-mcp-api";
    private readonly string _key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Existing_readonly_deployment_is_adopted_without_manual_key_copying_or_identity_rotation()
    {
        var environment = ExistingDeployment();
        environment["NetRatel__Mcp__Http__LocalCredentialMode"] = "true";
        environment["NetRatel__Mcp__Http__OperatorSurfaceEnabled"] = "true";
        environment["NetRatel__Mcp__Delegation__LifetimeSeconds"] = "120";
        environment["ASPNETCORE_URLS"] = "http://+:9444";
        var source = environment["NETRATEL_MCP_CONFIG"]!;
        var original = File.ReadAllText(source);

        var settings = ExistingHttpHostSettings.Discover(environment.GetValueOrDefault);
        Assert.False(Directory.Exists(Output)); // Discovery cannot manufacture successful provisioning.
        Assert.Equal("dev", settings.Instance);
        Assert.Equal(Api, settings.ApiBaseUrl);
        Assert.True(settings.LocalCredentialMode);
        Assert.True(settings.OperatorSurfaceEnabled);
        Assert.Equal(120, settings.DelegationLifetimeSeconds);
        Assert.Equal(9444, settings.ListeningPort);
        Assert.DoesNotContain(_key, JsonSerializer.Serialize(settings));
        Assert.DoesNotContain(_key, settings.ToString());

        CreatePrivateOutput();
        var keyPath = settings.MaterializeDelegationKey(Output);
        Assert.Equal(_key + "\n", File.ReadAllText(keyPath!));
        var before = File.GetLastWriteTimeUtc(keyPath!);
        Assert.Equal(keyPath, settings.MaterializeDelegationKey(Output));
        Assert.Equal(before, File.GetLastWriteTimeUtc(keyPath!));
        var request = new HttpHostingSetupRequest(Output, "https://web.example.invalid", settings.ApiBaseUrl!,
            Instance: settings.Instance!, LocalCredentialMode: settings.LocalCredentialMode!.Value,
            ExistingOutboundConfigurationPath: settings.OutboundConfigurationPath,
            ExistingDelegationKeyFile: keyPath, DelegationServicePrincipal: settings.DelegationServicePrincipal,
            DelegationKeyId: settings.DelegationKeyId, DelegationIssuer: settings.DelegationIssuer,
            DelegationAudience: settings.DelegationAudience, OperatorSurfaceEnabled: settings.OperatorSurfaceEnabled,
            DelegationLifetimeSeconds: settings.DelegationLifetimeSeconds!.Value, ListeningPort: settings.ListeningPort!.Value);
        var service = new NetRatelHttpHostingSetupService();
        var fingerprint = service.PrepareRegistration(request);
        var approved = request with { ApprovedRegistrationFingerprint = fingerprint };
        service.Preview(approved);
        service.Apply(approved);
        Assert.Equal(original, File.ReadAllText(source));
        Assert.Equal(_key + "\n", File.ReadAllText(keyPath!));
        Assert.False(File.Exists(Path.Combine(Output, "secrets", "outbound-m2m.secret")));
        Assert.False(File.Exists(Path.Combine(Output, "secrets", "delegation.key")));
        Assert.DoesNotContain(_key, File.ReadAllText(Path.Combine(Output, ".http-host.setup.json")));
        Assert.Contains("ServicePrincipal='" + Principal + "'", File.ReadAllText(Path.Combine(Output, "api.env")));
        Assert.Contains("LifetimeSeconds='120'", File.ReadAllText(Path.Combine(Output, "api.env")));
        Assert.Contains("NETRATEL_MCP_INSTANCE='dev'", File.ReadAllText(Path.Combine(Output, "mcp-http.env")));
        Assert.Contains("OperatorSurfaceEnabled='true'", File.ReadAllText(Path.Combine(Output, "mcp-http.env")));
        Assert.Contains("ASPNETCORE_URLS='http://+:9444'", File.ReadAllText(Path.Combine(Output, "mcp-http.env")));
        Assert.Contains("WebGateway__Mcp__BackendUrl='http://mcp-http:9444/'", File.ReadAllText(Path.Combine(Output, "web.env")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(source));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath!));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(keyPath!)!));
        }
    }

    [Fact]
    public void Ingress_oidc_is_explicit_and_independent_of_the_outbound_service_authentication()
    {
        var environment = ExistingDeployment();
        environment["NetRatel__Mcp__Http__LocalCredentialMode"] = "false";
        environment["NetRatel__Mcp__Http__Authority"] = "https://identity.example.invalid";
        environment["NetRatel__Mcp__Http__RequiredGroups__0"] = "netratel-operators";
        environment["NetRatel__Mcp__Http__RequiredScopes__0"] = "netratel.mcp";
        var settings = ExistingHttpHostSettings.Discover(environment.GetValueOrDefault);
        Assert.False(settings.LocalCredentialMode);
        Assert.Equal("https://identity.example.invalid", settings.OidcAuthority);
        Assert.Equal(["netratel-operators"], settings.RequiredGroups);
        Assert.Equal(["netratel.mcp"], settings.RequiredScopes);
        environment.Remove("NetRatel__Mcp__Http__LocalCredentialMode");
        Assert.Null(ExistingHttpHostSettings.Discover(environment.GetValueOrDefault).LocalCredentialMode);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void Existing_key_file_is_reused_and_never_exported_during_discovery()
    {
        var environment = ExistingDeployment();
        environment.Remove("NetRatel__Mcp__Delegation__SharedKeyBase64");
        var keyPath = Path.Combine(_directory, "managed-delegation.key");
        ConfigurationFileStorage.WriteAtomic(keyPath, _key + "\n");
        environment["NetRatel__Mcp__Delegation__SharedKeyBase64File"] = keyPath;
        environment["NETRATEL_MCP_DELEGATION_KEY_FILE"] = Path.Combine(_directory, "unused-legacy-alias.key");
        var settings = ExistingHttpHostSettings.Discover(environment.GetValueOrDefault);
        Assert.Equal(keyPath, settings.MaterializeDelegationKey(Output));
        Assert.False(Directory.Exists(Output));
        Assert.Equal(_key + "\n", File.ReadAllText(keyPath));
    }

    [Fact]
    public void Different_inline_generation_is_rejected_without_overwriting_the_protected_key()
    {
        var environment = ExistingDeployment();
        CreatePrivateOutput();
        var path = ExistingHttpHostSettings.Discover(environment.GetValueOrDefault).MaterializeDelegationKey(Output)!;
        var otherKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        environment["NetRatel__Mcp__Delegation__SharedKeyBase64"] = otherKey;
        var changed = ExistingHttpHostSettings.Discover(environment.GetValueOrDefault);
        var error = Assert.Throws<AgentClientValidationException>(() => changed.MaterializeDelegationKey(Output));
        Assert.Contains("will not overwrite", error.Message);
        Assert.DoesNotContain(_key, error.Message);
        Assert.DoesNotContain(otherKey, error.Message);
        Assert.Equal(_key + "\n", File.ReadAllText(path));
    }

    [Fact]
    public void Incomplete_or_competing_delegation_sources_fail_without_creating_a_new_generation()
    {
        var environment = ExistingDeployment();
        environment.Remove("NetRatel__Mcp__Delegation__ServicePrincipal");
        var error = Assert.Throws<AgentClientValidationException>(() => ExistingHttpHostSettings.Discover(environment.GetValueOrDefault));
        Assert.Contains("incomplete", error.Message);
        Assert.DoesNotContain(_key, error.Message);
        environment["NetRatel__Mcp__Delegation__ServicePrincipal"] = Principal;
        environment["NetRatel__Mcp__Delegation__SharedKeyBase64File"] = Path.Combine(_directory, "other.key");
        Assert.Contains("both an inline key", Assert.Throws<AgentClientValidationException>(() => ExistingHttpHostSettings.Discover(environment.GetValueOrDefault)).Message);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void Mismatched_deployment_target_or_principal_cannot_rebind_the_managed_identity()
    {
        var environment = ExistingDeployment();
        environment["NETRATEL_MCP_DEV_API_BASE_URL"] = "https://other-api.example.invalid";
        Assert.Contains("will not redirect", Assert.Throws<AgentClientValidationException>(() => ExistingHttpHostSettings.Discover(environment.GetValueOrDefault)).Message);
        environment["NETRATEL_MCP_DEV_API_BASE_URL"] = Api;
        environment["NetRatel__Mcp__Delegation__ServicePrincipal"] = "another-service";
        Assert.Contains("does not match", Assert.Throws<AgentClientValidationException>(() => ExistingHttpHostSettings.Discover(environment.GetValueOrDefault)).Message);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void Unknown_or_invalid_ingress_mode_is_never_inferred_from_outbound_credentials()
    {
        var environment = ExistingDeployment();
        Assert.Null(ExistingHttpHostSettings.Discover(environment.GetValueOrDefault).LocalCredentialMode);
        environment["NetRatel__Mcp__Http__LocalCredentialMode"] = "local";
        Assert.Contains("true or false", Assert.Throws<AgentClientValidationException>(() => ExistingHttpHostSettings.Discover(environment.GetValueOrDefault)).Message);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void Single_listener_port_is_preserved_and_multiple_listeners_are_not_guessed()
    {
        var environment = ExistingDeployment();
        environment["ASPNETCORE_HTTP_PORTS"] = "9444";
        Assert.Equal(9444, ExistingHttpHostSettings.Discover(environment.GetValueOrDefault).ListeningPort);
        environment["ASPNETCORE_URLS"] = "http://0.0.0.0:9555";
        Assert.Equal(9555, ExistingHttpHostSettings.Discover(environment.GetValueOrDefault).ListeningPort);
        environment["ASPNETCORE_URLS"] = "http://+:9444;http://+:9555";
        Assert.Contains("will not choose a port", Assert.Throws<AgentClientValidationException>(() => ExistingHttpHostSettings.Discover(environment.GetValueOrDefault)).Message);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void Dedicated_m2m_environment_is_snapshotted_privately_once_and_changed_credentials_cannot_rotate_it()
    {
        var environment = ExistingDeployment();
        environment.Remove("NETRATEL_MCP_CONFIG");
        environment["NETRATEL_MCP_OUTBOUND_API_BASE_URL"] = Api;
        environment["NETRATEL_MCP_OUTBOUND_API_M2M_TOKEN_URL"] = Api + "/connect/token";
        environment["NETRATEL_MCP_OUTBOUND_API_M2M_CLIENT_ID"] = Principal;
        environment["NETRATEL_MCP_OUTBOUND_API_M2M_CLIENT_SECRET"] = "existing-environment-service-secret";
        environment["NETRATEL_MCP_OUTBOUND_API_M2M_SCOPE"] = "netratel.api";
        var settings = ExistingHttpHostSettings.Discover(environment.GetValueOrDefault);
        Assert.True(settings.HasOutboundIdentity);
        Assert.Null(settings.OutboundConfigurationPath);
        Assert.Equal("dev", settings.Instance);
        Assert.Equal(Api, settings.ApiBaseUrl);
        Assert.False(Directory.Exists(Output));
        Assert.DoesNotContain("existing-environment-service-secret", JsonSerializer.Serialize(settings));
        CreatePrivateOutput();
        var path = settings.MaterializeOutboundConfiguration(Output)!;
        Assert.Equal("existing-environment-service-secret", NetRatelConfigurationService.ReadConfiguration(path).ApiM2MClientSecret);
        var before = File.GetLastWriteTimeUtc(path);
        Assert.Equal(path, settings.MaterializeOutboundConfiguration(Output));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        environment["NETRATEL_MCP_OUTBOUND_API_M2M_CLIENT_SECRET"] = "different-environment-service-secret";
        var changed = ExistingHttpHostSettings.Discover(environment.GetValueOrDefault);
        var error = Assert.Throws<AgentClientValidationException>(() => changed.MaterializeOutboundConfiguration(Output));
        Assert.Contains("will not overwrite", error.Message);
        Assert.DoesNotContain("different-environment-service-secret", error.Message);
        Assert.Equal("existing-environment-service-secret", NetRatelConfigurationService.ReadConfiguration(path).ApiM2MClientSecret);
    }

    [Fact]
    public void Dedicated_oidc_environment_preserves_its_external_secret_file_and_explicit_service_principal()
    {
        var environment = ExistingDeployment();
        environment.Remove("NETRATEL_MCP_CONFIG");
        var secretFile = Path.Combine(_directory, "managed-oidc-password");
        ConfigurationFileStorage.WriteAtomic(secretFile, "external-application-password\n");
        environment["NetRatel__Mcp__Outbound__ApiBaseUrl"] = Api;
        environment["NetRatel__Mcp__Outbound__AuthenticationMode"] = "Oidc";
        environment["NetRatel__Mcp__Outbound__OidcTokenUrl"] = "https://identity.example.invalid/connect/token?tenant=example";
        environment["NetRatel__Mcp__Outbound__OidcClientId"] = "existing-application";
        environment["NetRatel__Mcp__Outbound__OidcUsername"] = "existing-application-account";
        environment["NetRatel__Mcp__Outbound__OidcAppPasswordFile"] = secretFile;
        var settings = ExistingHttpHostSettings.Discover(environment.GetValueOrDefault);
        Assert.True(settings.HasOutboundIdentity);
        Assert.Equal(Principal, settings.DelegationServicePrincipal);
        Assert.Null(settings.LocalCredentialMode); // Outbound OIDC says nothing about client ingress.
        CreatePrivateOutput();
        var path = settings.MaterializeOutboundConfiguration(Output)!;
        var snapshot = NetRatelConfigurationService.ReadConfiguration(path);
        Assert.Equal(secretFile, snapshot.OidcAppPasswordFile);
        Assert.Null(snapshot.OidcAppPassword);
        Assert.DoesNotContain("external-application-password", File.ReadAllText(path));
        Assert.Equal("external-application-password\n", File.ReadAllText(secretFile));
        Assert.True(new AgentClientConfigurationValidator(ConfigurationSurface.HttpMcpOutbound)
            .Validate(null, NetRatelConfigurationService.ResolveSecretReferences(snapshot)).Succeeded);
    }

    [Fact]
    public void Incomplete_dedicated_environment_and_personal_credentials_cannot_become_a_host_identity()
    {
        var environment = ExistingDeployment();
        environment.Remove("NETRATEL_MCP_CONFIG");
        environment["NetRatel__Mcp__Outbound__ApiBaseUrl"] = Api;
        environment["NetRatel__Mcp__Outbound__ApiM2MClientId"] = Principal;
        Assert.Contains("incomplete or invalid", Assert.Throws<AgentClientValidationException>(() => ExistingHttpHostSettings.Discover(environment.GetValueOrDefault)).Message);
        environment.Remove("NetRatel__Mcp__Outbound__ApiM2MClientId");
        environment["NetRatel__Mcp__Outbound__AuthenticationMode"] = "IntegrationCredential";
        environment["NetRatel__Mcp__Outbound__IntegrationCredential"] = "nrt_ic_personal-must-not-be-adopted";
        var rejected = Assert.Throws<AgentClientValidationException>(() => ExistingHttpHostSettings.Discover(environment.GetValueOrDefault));
        Assert.Contains("human integration credential is not a gateway identity", rejected.Message);
        Assert.DoesNotContain("nrt_ic_personal", rejected.Message);
        environment.Remove("NetRatel__Mcp__Outbound__ApiBaseUrl");
        environment.Remove("NetRatel__Mcp__Outbound__AuthenticationMode");
        environment.Remove("NetRatel__Mcp__Outbound__IntegrationCredential");
        environment["NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN"] = "nrt_ic_unrelated-personal-credential";
        Assert.False(ExistingHttpHostSettings.Discover(environment.GetValueOrDefault).HasOutboundIdentity);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void Mixed_json_and_dedicated_environment_reuses_the_effective_secret_reference_and_rejects_target_rebinding()
    {
        var environment = ExistingDeployment();
        var originalPath = environment["NETRATEL_MCP_CONFIG"]!;
        var original = File.ReadAllText(originalPath);
        var secretFile = Path.Combine(_directory, "current-service.secret");
        ConfigurationFileStorage.WriteAtomic(secretFile, "current-effective-service-secret\n");
        environment["NetRatel__Mcp__Outbound__ApiM2MClientSecretFile"] = secretFile;
        var settings = ExistingHttpHostSettings.Discover(environment.GetValueOrDefault);
        Assert.Null(settings.OutboundConfigurationPath);
        Assert.True(settings.HasOutboundIdentity);
        Assert.False(Directory.Exists(Output));
        CreatePrivateOutput();
        var snapshotPath = settings.MaterializeOutboundConfiguration(Output)!;
        var snapshot = NetRatelConfigurationService.ReadConfiguration(snapshotPath);
        Assert.Equal(secretFile, snapshot.ApiM2MClientSecretFile);
        Assert.Null(snapshot.ApiM2MClientSecret);
        Assert.Equal("current-effective-service-secret", NetRatelConfigurationService.ResolveSecretReferences(snapshot).ApiM2MClientSecret);
        Assert.Equal(original, File.ReadAllText(originalPath));
        var snapshotBefore = File.ReadAllText(snapshotPath);
        environment["NetRatel__Mcp__Outbound__ApiBaseUrl"] = "https://other-api.example.invalid";
        environment["NetRatel__Mcp__Outbound__ApiM2MTokenUrl"] = "https://other-api.example.invalid/connect/token";
        var error = Assert.Throws<AgentClientValidationException>(() => ExistingHttpHostSettings.Discover(environment.GetValueOrDefault));
        Assert.Contains("reauthorization/rebind", error.Message);
        Assert.DoesNotContain("current-effective-service-secret", error.Message);
        Assert.Equal(snapshotBefore, File.ReadAllText(snapshotPath));
        Assert.Equal(original, File.ReadAllText(originalPath));
    }

    private void CreatePrivateOutput()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Output);
        else Directory.CreateDirectory(Output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private Dictionary<string, string?> ExistingDeployment()
    {
        var source = Path.Combine(_directory, "managed-outbound.json");
        if (!File.Exists(source))
        {
            var outbound = AgentClientConfiguration.Empty with
            {
                ApiBaseUrl = Api, ApiM2MTokenUrl = Api + "/connect/token", ApiM2MClientId = Principal,
                ApiM2MClientSecret = "existing-service-secret", ApiM2MScope = "netratel.api", Instance = "dev"
            };
            ConfigurationFileStorage.WriteAtomic(source, JsonSerializer.Serialize(outbound, AgentClientConfiguration.JsonOptions));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(source, UnixFileMode.UserRead);
        }
        return new(StringComparer.Ordinal)
        {
            ["NETRATEL_MCP_CONFIG"] = source, ["NETRATEL_MCP_INSTANCE"] = "dev", ["NETRATEL_MCP_DEV_API_BASE_URL"] = Api,
            ["NetRatel__Mcp__Delegation__Enabled"] = "true", ["NetRatel__Mcp__Delegation__SharedKeyBase64"] = _key,
            ["NetRatel__Mcp__Delegation__KeyId"] = "existing-v1", ["NetRatel__Mcp__Delegation__Issuer"] = "existing-mcp-dev",
            ["NetRatel__Mcp__Delegation__Audience"] = "existing-api-dev", ["NetRatel__Mcp__Delegation__ServicePrincipal"] = Principal
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

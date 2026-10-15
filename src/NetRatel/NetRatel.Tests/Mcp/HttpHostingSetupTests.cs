using System.Text.Json;
using NetRatel.AgentClient;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class HttpHostingSetupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netratel-http-host-" + Guid.NewGuid().ToString("N"));
    private HttpHostingSetupRequest Request => new(_directory, "https://netratel.example.invalid", "https://netratel.example.invalid");

    [Fact]
    public void Setup_derives_aligned_resources_and_reuses_independent_private_secrets()
    {
        var service = new NetRatelHttpHostingSetupService();
        var request = Request;
        var preview = service.Preview(request);
        Assert.False(Directory.Exists(_directory));
        Assert.Contains(preview.Changes, change => change.Contains("https://netratel.example.invalid/mcp", StringComparison.Ordinal));
        var result = service.Apply(request);
        var delegation = File.ReadAllText(Path.Combine(_directory, "secrets", "delegation.key"));
        var outbound = File.ReadAllText(Path.Combine(_directory, "secrets", "outbound-m2m.secret"));
        Assert.NotEqual(delegation, outbound);
        Assert.Equal(32, Convert.FromBase64String(delegation.Trim()).Length);
        Assert.Contains("Integration__HttpMcp__PublicResourceUri='https://netratel.example.invalid/mcp'", File.ReadAllText(Path.Combine(_directory, "api.env")));
        Assert.Contains("NetRatel__Mcp__Http__PublicResourceUri='https://netratel.example.invalid/mcp'", File.ReadAllText(Path.Combine(_directory, "mcp-http.env")));
        Assert.Contains("ApiM2MTokenUrl='https://netratel.example.invalid/connect/token'", File.ReadAllText(Path.Combine(_directory, "mcp-http.env")));
        Assert.Contains("M2M__AllowedCallerClientIds__2='netratel-mcp-http'", File.ReadAllText(Path.Combine(_directory, "api.env")));
        Assert.Contains("NETRATEL_CLIENT_DIRECTORY='/var/lib/netratel/clients'", File.ReadAllText(Path.Combine(_directory, "mcp-http.env")));
        Assert.DoesNotContain("NETRATEL_CLIENT_DIRECTORY", File.ReadAllText(Path.Combine(_directory, "api.env")));
        var overlay = File.ReadAllText(Path.Combine(_directory, "compose.http-host.yaml"));
        Assert.Contains("source: http-client-data\n        target: /var/lib/netratel/clients", overlay);
        Assert.Contains("NETRATEL_CLIENT_DIRECTORY", overlay);
        var timestamps = result.Files.ToDictionary(path => path, File.GetLastWriteTimeUtc);
        service.Preview(request);
        service.Apply(request);
        Assert.All(result.Files, path => Assert.Equal(timestamps[path], File.GetLastWriteTimeUtc(path)));
        Assert.Equal(delegation, File.ReadAllText(Path.Combine(_directory, "secrets", "delegation.key")));
        Assert.DoesNotContain(delegation.Trim(), JsonSerializer.Serialize(preview));
        Assert.DoesNotContain(outbound.Trim(), JsonSerializer.Serialize(preview));
        if (!OperatingSystem.IsWindows())
            Assert.All(result.Files, path => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite |
                (Path.GetDirectoryName(path) == Path.Combine(_directory, "runtime") ? UnixFileMode.GroupRead : 0), File.GetUnixFileMode(path)));
    }

    [Theory]
    [InlineData("https://netratel.example.invalid:443", "https://netratel.example.invalid")]
    [InlineData("https://netratel.example.invalid:8443", "https://netratel.example.invalid:8443")]
    public void Generated_browser_origins_use_browser_port_serialization_while_instance_authority_is_retained(string enteredOrigin, string browserOrigin)
    {
        var service = new NetRatelHttpHostingSetupService();
        var request = Request with { PublicOrigin = enteredOrigin + "/mcp", ApiBaseUrl = "" };
        service.Apply(request);
        var environment = File.ReadAllText(Path.Combine(_directory, "mcp-http.env"));
        Assert.Contains("AllowedOrigins__0='" + browserOrigin + "'", environment);
        Assert.Contains("PublicResourceUri='" + enteredOrigin + "/mcp'", environment);
        var advanced = request with { AllowedOrigins = [enteredOrigin] };
        Assert.DoesNotContain(service.Preview(advanced).Changes, change => change.StartsWith("Replace ", StringComparison.Ordinal));
        service.Apply(advanced);
        Assert.Equal(environment, File.ReadAllText(Path.Combine(_directory, "mcp-http.env")));
    }

    [Fact]
    public void Existing_output_requires_reviewed_replacement_before_secrets_are_created()
    {
        CreatePrivateDirectory(_directory);
        var existing = Path.Combine(_directory, "web.env");
        ConfigurationFileStorage.WriteAtomic(existing, "OWNER_MANAGED=keep-until-reviewed\n");
        var service = new NetRatelHttpHostingSetupService();
        var request = Request;
        var preview = service.Preview(request);
        Assert.Contains(preview.Changes, change => change.StartsWith("Replace ", StringComparison.Ordinal));
        Assert.Throws<AgentClientValidationException>(() => service.Apply(request));
        Assert.Equal("OWNER_MANAGED=keep-until-reviewed\n", File.ReadAllText(existing));
        Assert.False(Directory.Exists(Path.Combine(_directory, "secrets")));
        service.Apply(request, replace: true);
        Assert.Contains("WebGateway__Mcp__Enabled='true'", File.ReadAllText(existing));
    }

    [Fact]
    public void Changed_file_after_preview_requires_a_new_review()
    {
        var service = new NetRatelHttpHostingSetupService();
        var request = Request;
        service.Apply(request);
        service.Preview(request);
        File.AppendAllText(Path.Combine(_directory, "api.env"), "OWNER_MANAGED=updated\n");
        var exception = Assert.Throws<AgentClientValidationException>(() => service.Apply(request, replace: true));
        Assert.Contains("changed since review", exception.Message);
        var scopes = new List<string> { "netratel.mcp.read" };
        var mutable = request with { RequiredScopes = scopes };
        service.Preview(mutable);
        scopes.Add("netratel.mcp.admin");
        Assert.Contains("inputs changed since review", Assert.Throws<AgentClientValidationException>(() => service.Apply(mutable, replace: true)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Private_http_outbound_and_incomplete_oidc_fail_without_writing()
    {
        var service = new NetRatelHttpHostingSetupService();
        Assert.Throws<AgentClientValidationException>(() => service.Apply(Request with { ApiBaseUrl = "http://api:9222/" }));
        Assert.Throws<AgentClientValidationException>(() => service.Apply(Request with { LocalCredentialMode = false, OidcAuthority = "https://issuer.example.invalid" }));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Existing_json_identity_is_preserved_without_provisioning_a_different_secret()
    {
        CreatePrivateDirectory(_directory);
        var source = Path.Combine(_directory, "existing-outbound.json");
        const string credential = "existing-deployment-owned-secret";
        var configuration = AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://netratel.example.invalid", ApiM2MTokenUrl = "https://netratel.example.invalid/connect/token",
            ApiM2MClientId = "existing-mcp", ApiM2MClientSecret = credential, ApiM2MScope = "netratel.api", Instance = "prod"
        };
        ConfigurationFileStorage.WriteAtomic(source, JsonSerializer.Serialize(configuration, AgentClientConfiguration.JsonOptions));
        var keyPath = Path.Combine(_directory, "existing-delegation.key");
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        ConfigurationFileStorage.WriteAtomic(keyPath, key + "\n");
        var before = File.ReadAllText(source);
        var service = new NetRatelHttpHostingSetupService();
        var request = Request with
        {
            ExistingOutboundConfigurationPath = source, ExistingDelegationKeyFile = keyPath,
            DelegationKeyId = "preserved-v3", DelegationIssuer = "existing-issuer", DelegationAudience = "existing-audience"
        };
        Assert.Contains("Instance must match", Assert.Throws<AgentClientValidationException>(() => service.Preview(request with { Instance = "dev" })).Message);
        var preview = service.Preview(request);
        Assert.DoesNotContain(credential, JsonSerializer.Serialize(preview));
        Assert.DoesNotContain(key, JsonSerializer.Serialize(preview));
        service.Apply(request);
        Assert.Equal(before, File.ReadAllText(source));
        Assert.Equal(key + "\n", File.ReadAllText(keyPath));
        Assert.False(File.Exists(Path.Combine(_directory, "secrets", "delegation.key")));
        Assert.False(File.Exists(Path.Combine(_directory, "secrets", "outbound-m2m.secret")));
        Assert.DoesNotContain("M2MClients__", File.ReadAllText(Path.Combine(_directory, "api.env")));
        Assert.Contains("ServicePrincipal='existing-mcp'", File.ReadAllText(Path.Combine(_directory, "api.env")));
        Assert.Contains("KeyId='preserved-v3'", File.ReadAllText(Path.Combine(_directory, "api.env")));
        Assert.Contains("Issuer='existing-issuer'", File.ReadAllText(Path.Combine(_directory, "mcp-http.env")));
        Assert.Contains("Audience='existing-audience'", File.ReadAllText(Path.Combine(_directory, "mcp-http.env")));
        Assert.Contains(Path.Combine(_directory, "runtime", "delegation.key"), File.ReadAllText(Path.Combine(_directory, "compose.http-host.yaml")));
        Assert.Equal(key + "\n", File.ReadAllText(Path.Combine(_directory, "runtime", "delegation.key")));
        service.Preview(request);
        ConfigurationFileStorage.WriteAtomic(keyPath, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) + "\n", replace: true);
        Assert.Contains("changed since review", Assert.Throws<AgentClientValidationException>(() => service.Apply(request)).Message);
    }

    [Fact]
    public void Container_output_uses_the_reviewed_host_mount_path_in_compose()
    {
        var request = Request with { ComposeOutputDirectory = "/srv/netratel/http-host", ListeningPort = 9444, PublicResourceUri = "https://netratel.example.invalid/prefix/mcp" };
        new NetRatelHttpHostingSetupService().Apply(request);
        var overlay = File.ReadAllText(Path.Combine(_directory, "compose.http-host.yaml"));
        Assert.Contains("/srv/netratel/http-host/runtime/delegation.key", overlay);
        Assert.DoesNotContain("env_file: !override\n", overlay);
        Assert.Contains("env_file: !override []", overlay);
        Assert.DoesNotContain(_directory, overlay);
        Assert.DoesNotContain("environment: !reset {}", overlay);
        Assert.Contains("http://mcp-http:9444/", File.ReadAllText(Path.Combine(_directory, "web.env")));
        Assert.Contains("ReverseProxy__Routes__mcpRoute__Match__Path='/prefix/mcp'", File.ReadAllText(Path.Combine(_directory, "web.env")));
        Assert.Contains("ReverseProxy__Routes__mcpRoute__Transforms__0__PathSet='/mcp'", File.ReadAllText(Path.Combine(_directory, "web.env")));
        Assert.Contains("ASPNETCORE_URLS='http://+:9444'", File.ReadAllText(Path.Combine(_directory, "mcp-http.env")));
        Assert.Contains("http://127.0.0.1:9444/health/live", overlay);
        var preview = new NetRatelHttpHostingSetupService().Preview(request with { WebMcpBackendUrl = "https://private-mcp.example.invalid:9555/" });
        Assert.Contains(preview.Changes, change => change.Contains("https://private-mcp.example.invalid:9555/", StringComparison.Ordinal));
    }

    [Fact]
    public void Existing_file_referenced_identity_exports_readable_copies_without_changing_managed_source()
    {
        CreatePrivateDirectory(_directory);
        var secret = Path.Combine(_directory, "owner-managed.secret");
        var source = Path.Combine(_directory, "owner-managed.json");
        ConfigurationFileStorage.WriteAtomic(secret, "existing-outbound-service-credential\n");
        var configuration = AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://netratel.example.invalid", ApiM2MTokenUrl = "https://netratel.example.invalid/connect/token",
            ApiM2MClientId = "existing-service", ApiM2MClientSecretFile = secret, ApiM2MScope = "netratel.api", Instance = "prod"
        };
        ConfigurationFileStorage.WriteAtomic(source, JsonSerializer.Serialize(configuration, AgentClientConfiguration.JsonOptions));
        var before = File.ReadAllText(source);
        new NetRatelHttpHostingSetupService().Apply(Request with { ExistingOutboundConfigurationPath = source });
        Assert.Equal(before, File.ReadAllText(source));
        var exported = NetRatelConfigurationService.ReadConfiguration(Path.Combine(_directory, "runtime", "mcp-outbound.json"));
        Assert.Equal("/run/netratel-mcp-secrets/outbound-secret-0", exported.ApiM2MClientSecretFile);
        Assert.Equal(File.ReadAllText(secret), File.ReadAllText(Path.Combine(_directory, "runtime", "outbound-secret-0")));
        Assert.Contains(Path.Combine(_directory, "runtime", "outbound-secret-0"), File.ReadAllText(Path.Combine(_directory, "compose.http-host.yaml")));
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(_directory, "compose.http-host.yaml")));
    }

    [Theory]
    [InlineData("https://netratel.example.invalid", "https://netratel.example.invalid")]
    [InlineData("https://netratel.example.invalid/", "https://netratel.example.invalid")]
    [InlineData("https://netratel.example.invalid/mcp", "https://netratel.example.invalid")]
    [InlineData("https://netratel.example.invalid/mcp/", "https://netratel.example.invalid")]
    [InlineData("https://netratel.example.invalid:443/mcp", "https://netratel.example.invalid:443")]
    [InlineData("https://netratel.example.invalid:8443/mcp", "https://netratel.example.invalid:8443")]
    public void Instance_address_normalizes_only_supported_public_forms(string input, string origin)
    {
        var address = InstanceSetupAddress.Parse(input);
        Assert.Equal(origin, address.Origin);
        Assert.Equal(origin + "/mcp", address.McpResource);
    }

    [Theory]
    [InlineData("http://netratel.example.invalid")]
    [InlineData("https://person:secret@netratel.example.invalid/mcp")]
    [InlineData("https://netratel.example.invalid/mcp?access_token=secret")]
    [InlineData("https://netratel.example.invalid/#fragment")]
    [InlineData("https://netratel.example.invalid/prefix/mcp")]
    [InlineData("https://netratel.example.invalid/a/../mcp")]
    public void Instance_address_rejects_credential_destinations_which_would_be_silently_changed(string input)
        => Assert.Throws<AgentClientValidationException>(() => InstanceSetupAddress.Parse(input));

    [Fact]
    public void Approval_binds_full_effective_settings_and_reuses_generation_without_reprovisioning()
    {
        var service = new NetRatelHttpHostingSetupService();
        var request = Request with { PublicOrigin = "https://netratel.example.invalid/mcp", ApiBaseUrl = "" };
        var fingerprint = service.PrepareRegistration(request);
        Assert.False(File.Exists(Path.Combine(_directory, "api.env")));
        var secret = File.ReadAllText(Path.Combine(_directory, "secrets", "outbound-m2m.secret"));
        Assert.Equal(fingerprint, service.PrepareRegistration(request));
        Assert.NotEqual(fingerprint, service.PrepareRegistration(request with { ListeningPort = 9444 }));
        Assert.NotEqual(fingerprint, service.PrepareRegistration(request with { LocalCredentialMode = false,
            OidcAuthority = "https://issuer.example.invalid", RequiredGroups = ["mcp-users"], RequiredScopes = ["netratel.mcp"] }));
        var approved = request with { ApprovedRegistrationFingerprint = fingerprint };
        service.Preview(approved);
        service.Apply(approved);
        Assert.Equal(secret, File.ReadAllText(Path.Combine(_directory, "secrets", "outbound-m2m.secret")));
        Assert.Equal(secret, File.ReadAllText(Path.Combine(_directory, "runtime", "outbound-m2m.secret")));
        Assert.Contains("group_add:", File.ReadAllText(Path.Combine(_directory, "compose.http-host.yaml")));
        Assert.Contains("up -d web api mcp-http", NetRatelHttpHostingSetupService.ApplyCommand(approved));
        var resumed = NetRatelHttpHostingSetupService.LoadRequest(_directory)! with
        {
            // After recreation discovery includes the dedicated host registration.
            ExistingM2MCallerClientIds = ["netratel.cli", "netratel.client.artifacts", "netratel-mcp-http"]
        };
        Assert.Equal(fingerprint, service.PrepareRegistration(resumed));
        Assert.DoesNotContain(service.Preview(resumed).Changes, change => change.StartsWith("Replace ", StringComparison.Ordinal));
        service.Apply(resumed);
        service.Preview(approved with { ListeningPort = 9444 });
        Assert.Contains("changed after administrator approval", Assert.Throws<AgentClientValidationException>(() => service.Apply(approved with { ListeningPort = 9444 }, replace: true)).Message);
    }

    [Fact]
    public void New_host_registration_preserves_existing_api_m2m_issuer_audience_and_callers()
    {
        var service = new NetRatelHttpHostingSetupService();
        var request = Request with { ApiServiceAuthority = "https://issuer.example.invalid", ApiServiceAudience = "custom.api",
            ExistingM2MCallerClientIds = ["custom.worker", "custom.reports", "netratel.cli"] };
        service.Apply(request);
        var api = File.ReadAllText(Path.Combine(_directory, "api.env"));
        var mcp = File.ReadAllText(Path.Combine(_directory, "mcp-http.env"));
        Assert.DoesNotContain("M2M__Authority=", api);
        Assert.DoesNotContain("M2M__Audience=", api);
        Assert.Contains("M2M__AllowedCallerClientIds__0='custom.worker'", api);
        Assert.Contains("M2M__AllowedCallerClientIds__1='custom.reports'", api);
        Assert.Contains("M2M__AllowedCallerClientIds__2='netratel.cli'", api);
        Assert.Contains("M2M__AllowedCallerClientIds__3='netratel-mcp-http'", api);
        Assert.Contains("M2MClients__netratel_mcp_http__AllowedAudiences__0='custom.api'", api);
        Assert.Contains("M2MClients__netratel_mcp_http__AllowedScopes__0='custom.api'", api);
        Assert.Contains("ApiM2MScope='custom.api'", mcp);
        var saved = NetRatelHttpHostingSetupService.LoadRequest(_directory);
        Assert.Equal(request.ApiServiceAudience, saved!.ApiServiceAudience);
        Assert.Equal(request.ApiServiceAuthority, saved.ApiServiceAuthority);
    }

    [Theory]
    [InlineData("delegation.key")]
    [InlineData("outbound-m2m.secret")]
    public void Missing_original_in_a_saved_generation_requires_recovery_without_automatic_rotation(string file)
    {
        var service = new NetRatelHttpHostingSetupService();
        service.Apply(Request);
        var original = Path.Combine(_directory, "secrets", file);
        var backup = File.ReadAllText(Path.Combine(_directory, "runtime", file));
        File.Delete(original);
        Assert.Contains("same generation", Assert.Throws<AgentClientValidationException>(() => service.PrepareRegistration(Request)).Message);
        Assert.False(File.Exists(original));
        Assert.Equal(backup, File.ReadAllText(Path.Combine(_directory, "runtime", file)));
    }

    [Fact]
    public void Saved_advanced_host_settings_resume_the_same_identity_and_reject_instance_rebinding()
    {
        var service = new NetRatelHttpHostingSetupService();
        var request = Request with { LocalCredentialMode = false, OidcAuthority = "https://issuer.example.invalid",
            RequiredGroups = ["mcp-users"], RequiredScopes = ["netratel.mcp"],
            PublicResourceUri = "https://netratel.example.invalid/prefix/mcp", ListeningPort = 9444,
            WebMcpBackendUrl = "https://private-mcp.example.invalid:9444/", OperatorSurfaceEnabled = false };
        service.Apply(request);
        var saved = Assert.IsType<HttpHostingSetupRequest>(NetRatelHttpHostingSetupService.LoadRequest(_directory, "https://netratel.example.invalid/mcp/"));
        Assert.False(saved.LocalCredentialMode);
        Assert.Equal(request.OidcAuthority, saved.OidcAuthority);
        Assert.Equal(request.PublicResourceUri, saved.PublicResourceUri);
        Assert.Equal(request.WebMcpBackendUrl, saved.WebMcpBackendUrl);
        Assert.Equal(request.ListeningPort, saved.ListeningPort);
        Assert.Equal(request.RequiredGroups, saved.RequiredGroups);
        Assert.Null(saved.ApprovedRegistrationFingerprint);
        service.Preview(saved);
        service.Apply(saved);
        Assert.Contains("another instance", Assert.Throws<AgentClientValidationException>(() => NetRatelHttpHostingSetupService.LoadRequest(_directory, "https://another.example.invalid")).Message);
    }

    [Fact]
    public void Runtime_exports_retain_original_owner_only_generation_and_detect_tampering()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = new NetRatelHttpHostingSetupService();
        service.Apply(Request);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_directory, "secrets", "delegation.key")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(Path.Combine(_directory, "runtime", "delegation.key")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
            File.GetUnixFileMode(Path.Combine(_directory, "runtime")));
        ConfigurationFileStorage.WriteAtomic(Path.Combine(_directory, "runtime", "delegation.key"), "different generation\n", replace: true);
        Assert.Contains("runtime export does not match", Assert.Throws<AgentClientValidationException>(() => service.Apply(Request)).Message);
        Assert.Throws<AgentClientValidationException>(() => HttpHostingStorage.Resolve(Path.GetPathRoot(_directory)!));
    }

    [Fact]
    public void Broad_native_output_is_rejected_before_identity_or_runtime_secret_creation()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_directory);
        var broad = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(_directory, broad);
        var service = new NetRatelHttpHostingSetupService();
        Assert.Contains("owner-only output directory", Assert.Throws<AgentClientValidationException>(() => service.PrepareRegistration(Request)).Message);
        Assert.Throws<AgentClientValidationException>(() => service.Apply(Request));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
        Assert.Equal(broad, File.GetUnixFileMode(_directory));
        File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var preview = service.Preview(Request);
        Assert.Contains(preview.Changes, change => change.Contains("container GID", StringComparison.Ordinal) && change.Contains("owner-only (0700)", StringComparison.Ordinal));
        service.Apply(Request);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_directory));
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

using System.Text.Json;
using NetRatel.AgentClient;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class HttpHostingComposeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netratel-komodo-" + Guid.NewGuid().ToString("N"));
    private const string Compose = """
        name: existing-stack
        services:
          web:
            image: approved/web@sha256:abc
            env_file: [/opt/stack/web.env]
            ports: ['8443:8080']
            labels:
              ingress.route: existing
          api:
            image: approved/api@sha256:def
            env_file: [/opt/stack/api.env]
            group_add: ['123']
            environment:
              Database__Provider: PostgreSql
              M2MClients__unrelated__SecretFile: /run/secrets/unrelated
            volumes: [api-data:/data]
          mcp-http:
            image: approved/mcp@sha256:ghi
            env_file: [/opt/stack/mcp.env]
            environment:
              - UNRELATED_OPTION=keep
              - NETRATEL_MCP_INSTANCE=dev
            volumes:
              - type: bind
                source: /opt/stack/outbound.json
                target: /run/secrets/mcp-outbound-config
                read_only: true
            networks:
              default:
                aliases: [customer-mcp]
          postgres:
            image: postgres:17-alpine
            volumes: [postgres-data:/var/lib/postgresql/data]
        volumes:
          api-data:
          postgres-data:
        """;

    private static readonly Dictionary<string, string> Web = new() { ["WebGateway__Mcp__BackendUrl"] = "http://mcp-http:9224/" };
    private static readonly Dictionary<string, string> Api = new() { ["NetRatel__Mcp__Delegation__SharedKeyBase64File"] = "/run/netratel-http-host/runtime/delegation.key" };
    private static readonly Dictionary<string, string> Mcp = new() { ["NETRATEL_MCP_INSTANCE"] = "dev", ["NETRATEL_CLIENT_DIRECTORY"] = "/var/lib/netratel/clients" };
    private static string Merge(string source) => HttpHostingComposeDocument.Merge(source, Web, Api, Mcp, 10001,
        "existing-stack-host", "existing-stack-clients", 9224, "existing-stack");

    [Fact]
    public void Komodo_import_preserves_complete_deployment_and_is_idempotent_after_editor_save()
    {
        var merged = Merge(Compose);
        Assert.Equal(merged, Merge(merged));
        Assert.Contains("/opt/stack/web.env", merged);
        Assert.Contains("/opt/stack/api.env", merged);
        Assert.Contains("/opt/stack/mcp.env", merged);
        Assert.Contains("8443:8080", merged);
        Assert.Contains("ingress.route: existing", merged);
        Assert.Contains("M2MClients__unrelated__SecretFile: /run/secrets/unrelated", merged);
        Assert.Contains("Database__Provider: PostgreSql", merged);
        Assert.Contains("UNRELATED_OPTION: \"keep\"", merged);
        Assert.Contains("postgres:17-alpine", merged);
        Assert.Contains("postgres-data:/var/lib/postgresql/data", merged);
        Assert.Contains("customer-mcp", merged);
        Assert.Contains("/opt/stack/outbound.json", merged);
        Assert.Contains("/run/secrets/mcp-outbound-config", merged);
        Assert.Contains("/setup-output", merged);
        Assert.Contains("/var/lib/netratel/clients", merged);
        Assert.Contains("existing-stack-host", merged);
        Assert.Contains("existing-stack-clients", merged);
        Assert.DoesNotContain("!override", merged);
        Assert.DoesNotContain("docker.sock", merged);
    }

    [Fact]
    public void Existing_named_mounts_are_reused_instead_of_replaced()
    {
        var source = Compose.Replace("volumes:\n      - type: bind", "volumes:\n      - current-host:/setup-output\n      - current-client:/var/lib/netratel/clients\n      - type: bind", StringComparison.Ordinal) +
            "\n  current-host:\n    name: existing-stack-host\n  current-client:\n    name: existing-stack-clients\n";
        var merged = Merge(source);
        Assert.Contains("source: \"current-host\"", merged);
        Assert.DoesNotContain("http-host-setup:", merged);
        Assert.Equal(merged, Merge(merged));
    }

    [Fact]
    public void Existing_project_default_volume_cannot_be_mistaken_for_a_different_launcher_volume()
    {
        var source = Compose.Replace("volumes:\n      - type: bind", "volumes:\n      - http-host-setup:/setup-output\n      - type: bind", StringComparison.Ordinal) + "\n  http-host-setup:\n";
        var exception = Assert.Throws<AgentClientValidationException>(() => Merge(source));
        Assert.Contains("does not match", exception.Message);
        Assert.DoesNotContain("existing-stack-host", exception.Message);
    }

    [Fact]
    public void Default_web_backend_inputs_preserve_existing_private_destinations_and_ports()
    {
        var source = Compose.Replace("    labels:\n", "    environment:\n      ApiBaseUrl: http://private-api:9333/\n      WebGateway__Mcp__BackendUrl: http://private-mcp:9444/\n      WebGateway__Topology: SplitHost\n    labels:\n", StringComparison.Ordinal);
        var proposed = new Dictionary<string, string>
        {
            ["ApiBaseUrl"] = "http://api:9222/", ["WebGateway__Mcp__BackendUrl"] = "http://mcp-http:9224/",
            ["WebGateway__Mcp__Enabled"] = "true"
        };
        var selected = HttpHostingComposeDocument.SelectWebBackends(source, proposed, preserveApi: true, preserveMcp: true);
        Assert.Equal("http://private-api:9333/", selected.ApiBackend);
        Assert.Equal("http://private-mcp:9444/", selected.McpBackend);
        Assert.False(selected.Changes.ContainsKey("ApiBaseUrl"));
        Assert.False(selected.Changes.ContainsKey("WebGateway__Mcp__BackendUrl"));
        var merged = HttpHostingComposeDocument.Merge(source, selected.Changes, Api, Mcp, 10001,
            "existing-stack-host", "existing-stack-clients", 9444, "existing-stack");
        Assert.Contains("ApiBaseUrl: http://private-api:9333/", merged);
        Assert.Contains("WebGateway__Mcp__BackendUrl: http://private-mcp:9444/", merged);
        Assert.Contains("WebGateway__Topology: SplitHost", merged);
        Assert.Contains("WebGateway__Mcp__Enabled: \"true\"", merged);
        Assert.Contains("/opt/stack/web.env", merged);
    }

    [Fact]
    public void Observed_managed_web_backends_are_kept_in_their_env_file_without_default_shadowing()
    {
        var proposed = new Dictionary<string, string>
        {
            ["ApiBaseUrl"] = "http://api:9222/", ["WebGateway__Mcp__BackendUrl"] = "http://mcp-http:9224/",
            ["WebGateway__Mcp__Enabled"] = "true"
        };
        var selected = HttpHostingComposeDocument.SelectWebBackends(Compose, proposed, preserveApi: true, preserveMcp: true,
            observedApi: "http://private-api:9333/", observedMcp: "http://private-mcp:9444/");
        Assert.Equal("http://private-api:9333/", selected.ApiBackend);
        Assert.Equal("http://private-mcp:9444/", selected.McpBackend);
        var merged = HttpHostingComposeDocument.Merge(Compose, selected.Changes, Api, Mcp, 10001,
            "existing-stack-host", "existing-stack-clients", 9444, "existing-stack");
        Assert.DoesNotContain("ApiBaseUrl", merged);
        Assert.DoesNotContain("WebGateway__Mcp__BackendUrl", merged);
        Assert.Contains("/opt/stack/web.env", merged);
        Assert.Contains("WebGateway__Mcp__Enabled: \"true\"", merged);
        Assert.Contains("Advanced", Assert.Throws<AgentClientValidationException>(() =>
            HttpHostingComposeDocument.SelectWebBackends(Compose, proposed, preserveApi: true, preserveMcp: true)).Message);
    }

    [Fact]
    public void Known_absent_optional_mcp_backend_can_be_added_and_reimported_without_changing_the_document()
    {
        var proposed = new Dictionary<string, string>
        {
            ["ApiBaseUrl"] = "http://api:9222/", ["WebGateway__Mcp__BackendUrl"] = "http://mcp-http:9444/",
            ["WebGateway__Mcp__Enabled"] = "true"
        };
        var unknown = Assert.Throws<AgentClientValidationException>(() => HttpHostingComposeDocument.SelectWebBackends(
            Compose, proposed, preserveApi: true, preserveMcp: true, observedApi: "http://private-api:9333/"));
        Assert.Contains("MCP", unknown.Message);
        var first = HttpHostingComposeDocument.SelectWebBackends(Compose, proposed, preserveApi: true, preserveMcp: true,
            observedApi: "http://private-api:9333/", environmentObserved: true);
        Assert.False(first.Changes.ContainsKey("ApiBaseUrl"));
        Assert.Equal("http://mcp-http:9444/", first.Changes["WebGateway__Mcp__BackendUrl"]);
        var imported = HttpHostingComposeDocument.Merge(Compose, first.Changes, Api, Mcp, 10001,
            "existing-stack-host", "existing-stack-clients", 9444, "existing-stack");
        var second = HttpHostingComposeDocument.SelectWebBackends(imported, proposed, preserveApi: true, preserveMcp: true,
            observedApi: "http://private-api:9333/", observedMcp: "http://mcp-http:9444/", environmentObserved: true);
        Assert.False(second.Changes.ContainsKey("WebGateway__Mcp__BackendUrl"));
        Assert.Equal(imported, HttpHostingComposeDocument.Merge(imported, second.Changes, Api, Mcp, 10001,
            "existing-stack-host", "existing-stack-clients", 9444, "existing-stack"));
        Assert.Contains("/opt/stack/web.env", imported);
    }

    [Theory]
    [InlineData("services:\n  web: &service {}\n  api: *service\n  mcp-http: {}")]
    [InlineData("include: other.yaml\nservices:\n  web: {}\n  api: {}\n  mcp-http: {}")]
    [InlineData("services:\n  web: {extends: other}\n  api: {}\n  mcp-http: {}")]
    [InlineData("services:\n  web: {}\n  api: {}\n  mcp-http:\n    environment: [OPTION=first, option=second]")]
    [InlineData("services:\n  web: {}\n  api: {}\n  mcp-http:\n    volumes: [wrong:/run/netratel-http-host]")]
    public void Unsupported_or_conflicting_sources_fail_before_any_mutation(string source)
    {
        var exception = Assert.Throws<AgentClientValidationException>(() => Merge(source));
        Assert.Contains("Komodo Compose preflight", exception.Message);
        Assert.Contains("No existing file was changed", exception.Message);
    }

    [Fact]
    public void Parser_errors_do_not_echo_source_secret_values()
    {
        const string value = "sensitive-owner-supplied-value";
        var exception = Assert.Throws<AgentClientValidationException>(() => Merge("services: [" + value));
        Assert.DoesNotContain(value, exception.Message);
    }

    [Fact]
    public void Reviewed_local_outbound_adoption_registers_the_same_secret_and_keeps_unrelated_callers()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var source = Path.Combine(_directory, "managed-outbound.json");
        const string secret = "existing-owner-managed-service-secret-value";
        var outbound = AgentClientConfiguration.Empty with
        {
            Instance = "dev", ApiBaseUrl = "https://api.example.invalid", ApiM2MTokenUrl = "https://api.example.invalid/connect/token",
            ApiM2MClientId = "custom-test-mcp-api", ApiM2MClientSecret = secret, ApiM2MScope = "custom.api"
        };
        ConfigurationFileStorage.WriteAtomic(source, JsonSerializer.Serialize(outbound, AgentClientConfiguration.JsonOptions));
        File.SetUnixFileMode(source, UnixFileMode.UserRead);
        var before = File.ReadAllBytes(source);
        var request = new HttpHostingSetupRequest(_directory, "https://web.example.invalid", "https://api.example.invalid", Instance: "dev",
            ExistingOutboundConfigurationPath: source, ApiServiceAuthority: "https://web.example.invalid", ApiServiceAudience: "custom.api",
            ExistingM2MCallerClientIds: ["unrelated.worker"], DelegationLifetimeSeconds: 60);
        var service = new NetRatelHttpHostingSetupService();
        var fingerprint = service.PrepareRegistration(request);
        var approved = request with { ApprovedRegistrationFingerprint = fingerprint };
        var preview = service.Preview(approved);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(preview));
        service.Apply(approved);
        var api = File.ReadAllText(Path.Combine(_directory, "api.env"));
        Assert.Contains("M2MClients__custom_test_mcp_api__SecretFile='/run/netratel-mcp-secrets/adopted-outbound-m2m.secret'", api);
        Assert.Contains("M2M__AllowedCallerClientIds__0='unrelated.worker'", api);
        Assert.Contains("M2M__AllowedCallerClientIds__1='custom-test-mcp-api'", api);
        Assert.Contains("LifetimeSeconds='60'", api);
        Assert.Equal(secret + "\n", File.ReadAllText(Path.Combine(_directory, "runtime", "adopted-outbound-m2m.secret")));
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(_directory, "secrets", "outbound-m2m.secret")));
        Assert.DoesNotContain(secret, api);
        Assert.Equal(fingerprint, new NetRatelHttpHostingSetupService().PrepareRegistration(NetRatelHttpHostingSetupService.LoadRequest(_directory)!));
    }

    [Fact]
    public void Existing_external_oidc_outbound_is_not_registered_as_a_local_api_identity()
    {
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var source = Path.Combine(_directory, "external-outbound.json");
        var outbound = AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://api.example.invalid", AuthenticationMode = "Oidc",
            OidcTokenUrl = "https://identity.example.invalid/oauth/token", OidcClientId = "external-mcp-application",
            OidcUsername = "external-mcp-service-account", OidcAppPassword = "external-owner-managed-service-secret-value",
            OidcScope = "openid external.api"
        };
        ConfigurationFileStorage.WriteAtomic(source, JsonSerializer.Serialize(outbound, AgentClientConfiguration.JsonOptions));
        var original = File.ReadAllBytes(source);
        new NetRatelHttpHostingSetupService().Apply(new(_directory, "https://web.example.invalid", "https://api.example.invalid",
            ExistingOutboundConfigurationPath: source, DelegationServicePrincipal: "external-mcp",
            ApiServiceAuthority: "https://web.example.invalid", ExistingM2MCallerClientIds: ["unrelated.worker"]));
        Assert.DoesNotContain("M2MClients__", File.ReadAllText(Path.Combine(_directory, "api.env")));
        Assert.False(File.Exists(Path.Combine(_directory, "runtime", "adopted-outbound-m2m.secret")));
        Assert.Equal(original, File.ReadAllBytes(source));
        var exported = NetRatelConfigurationService.ReadConfiguration(Path.Combine(_directory, "runtime", "mcp-outbound.json"));
        Assert.Equal(outbound, exported);
    }

    [Fact]
    public void A_previously_allowed_local_caller_requires_explicit_registration_replacement_review()
    {
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var source = Path.Combine(_directory, "supplied-outbound.json");
        var outbound = AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://api.example.invalid", ApiM2MTokenUrl = "https://api.example.invalid/connect/token",
            ApiM2MClientId = "existing-mcp", ApiM2MClientSecret = "existing-owner-managed-service-secret-value", ApiM2MScope = "netratel.api"
        };
        ConfigurationFileStorage.WriteAtomic(source, JsonSerializer.Serialize(outbound, AgentClientConfiguration.JsonOptions));
        var request = new HttpHostingSetupRequest(_directory, "https://web.example.invalid", "https://api.example.invalid",
            ExistingOutboundConfigurationPath: source, ApiServiceAuthority: "https://web.example.invalid", ExistingM2MCallerClientIds: ["existing-mcp", "unrelated.worker"]);
        var service = new NetRatelHttpHostingSetupService();
        Assert.Contains(service.Preview(request).Changes, change => change.StartsWith("Replace API service registration", StringComparison.Ordinal));
        Assert.Contains("explicitly confirm", Assert.Throws<AgentClientValidationException>(() => service.Apply(request)).Message);
        Assert.False(Directory.Exists(Path.Combine(_directory, "secrets")));
        Assert.False(File.Exists(Path.Combine(_directory, "api.env")));
        service.Apply(request, replace: true);
        Assert.Contains("M2M__AllowedCallerClientIds__1='unrelated.worker'", File.ReadAllText(Path.Combine(_directory, "api.env")));
    }

    [Fact]
    public void Ambiguous_environment_registration_alias_does_not_rename_or_provision_the_identity()
    {
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var source = Path.Combine(_directory, "supplied-outbound.json");
        var outbound = AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://api.example.invalid", ApiM2MTokenUrl = "https://api.example.invalid/connect/token",
            ApiM2MClientId = "existing--mcp", ApiM2MClientSecret = "existing-owner-managed-service-secret-value", ApiM2MScope = "netratel.api"
        };
        ConfigurationFileStorage.WriteAtomic(source, JsonSerializer.Serialize(outbound, AgentClientConfiguration.JsonOptions));
        var request = new HttpHostingSetupRequest(_directory, "https://web.example.invalid", "https://api.example.invalid",
            ExistingOutboundConfigurationPath: source, ApiServiceAuthority: "https://web.example.invalid", ExistingM2MCallerClientIds: []);
        Assert.Contains("unambiguous deployment registration alias", Assert.Throws<AgentClientValidationException>(() => new NetRatelHttpHostingSetupService().PrepareRegistration(request)).Message);
        Assert.False(Directory.Exists(Path.Combine(_directory, "secrets")));
        Assert.False(File.Exists(Path.Combine(_directory, "api.env")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

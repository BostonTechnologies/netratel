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
        var timestamps = result.Files.ToDictionary(path => path, File.GetLastWriteTimeUtc);
        service.Preview(request);
        service.Apply(request);
        Assert.All(result.Files, path => Assert.Equal(timestamps[path], File.GetLastWriteTimeUtc(path)));
        Assert.Equal(delegation, File.ReadAllText(Path.Combine(_directory, "secrets", "delegation.key")));
        Assert.DoesNotContain(delegation.Trim(), JsonSerializer.Serialize(preview));
        Assert.DoesNotContain(outbound.Trim(), JsonSerializer.Serialize(preview));
        if (!OperatingSystem.IsWindows())
            Assert.All(result.Files, path => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path)));
    }

    [Fact]
    public void Existing_output_requires_reviewed_replacement_before_secrets_are_created()
    {
        Directory.CreateDirectory(_directory);
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
        Directory.CreateDirectory(_directory);
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
        Assert.Contains(keyPath, File.ReadAllText(Path.Combine(_directory, "compose.http-host.yaml")));
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
        Assert.Contains("/srv/netratel/http-host/web.env", overlay);
        Assert.Contains("/srv/netratel/http-host/secrets/delegation.key", overlay);
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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

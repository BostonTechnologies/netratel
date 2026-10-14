using Microsoft.Extensions.Configuration;
using NetRatel.API.Security.M2M;
using NetRatel.Shared.Connectivity;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class DeploymentSecretFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netratel-secret-test-" + Guid.NewGuid().ToString("N"));

    public DeploymentSecretFileTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Deployment_service_identity_reads_explicit_file_and_preserves_its_scope()
    {
        var secret = Write("m2m.secret", new string('s', 40));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["M2M:Authority"] = "https://netratel.example.invalid",
            ["M2M:Audience"] = "netratel.api",
            ["M2M:AllowedCallerClientIds:0"] = "netratel-mcp-http",
            ["M2MClients:netratel-mcp-http:SecretFile"] = secret,
            ["M2MClients:netratel-mcp-http:AllowedAudiences:0"] = "netratel.api",
            ["M2MClients:netratel-mcp-http:AllowedScopes:0"] = "netratel.api"
        }).Build();
        var profile = new M2MDeploymentProfileResolver(configuration).Resolve("netratel-mcp-http");
        Assert.NotNull(profile);
        Assert.Equal(new string('s', 40), profile.Secret);
        Assert.Equal(["netratel.api"], profile.AllowedScopes);
    }

    [Fact]
    public void Inline_and_file_inputs_conflict_before_authentication()
    {
        var file = Write("secret", "must-not-appear-in-errors");
        var registration = new M2MClientRegistration { Secret = "inline-secret", SecretFile = file };
        var exception = Assert.Throws<InvalidOperationException>(() => registration.Secret);
        Assert.DoesNotContain("inline-secret", exception.ToString());
        Assert.DoesNotContain("must-not-appear", exception.ToString());
        var options = Delegation(file);
        options.SharedKeyBase64 = Convert.ToBase64String(new byte[32]);
        Assert.Throws<InvalidOperationException>(options.EnsureValid);
    }

    [Fact]
    public void Key_file_is_a_startup_snapshot_and_new_process_picks_up_explicit_rotation()
    {
        var file = Write("delegation.key", Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()));
        var options = Delegation(file);
        var service = new McpOperatorDelegationTokenService(options);
        var identity = new McpOperatorDelegationIdentity("operator", null, null, [], [], ["netratel.mcp.read"]);
        var assertion = service.Create(identity, "netratel_server_v2", "status", "test-request");
        File.WriteAllText(file, Convert.ToBase64String(Enumerable.Repeat((byte)8, 32).ToArray()));
        Assert.True(service.TryValidate(assertion, out _));
        Assert.False(new McpOperatorDelegationTokenService(Delegation(file)).TryValidate(assertion, out _));
    }

    [Fact]
    public void Missing_oversized_or_symbolic_secret_files_fail_without_fallback()
    {
        Assert.Throws<InvalidOperationException>(() => SecretFileReference.Read(Path.Combine(_directory, "absent"), "secret"));
        var file = Write("large", new string('x', 65_537));
        Assert.Throws<InvalidOperationException>(() => SecretFileReference.Read(file, "secret"));
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(_directory, "link");
            File.CreateSymbolicLink(link, file);
            Assert.Throws<InvalidOperationException>(() => SecretFileReference.Read(link, "secret"));
        }
    }

    [Fact]
    public void Canonical_file_key_beats_compact_alias()
    {
        var file = Write("valid", Convert.ToBase64String(new byte[32]));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{McpOperatorDelegationOptions.SectionName}:SharedKeyBase64File"] = file,
            ["NETRATEL_MCP_DELEGATION_KEY_FILE"] = Path.Combine(_directory, "missing")
        }).Build();
        Assert.Equal(file, McpOperatorDelegationOptions.FromConfiguration(config).SharedKeyBase64File);
    }

    [Fact]
    public void Higher_secret_file_layer_replaces_lower_inline_material_for_delegation_and_service_identity()
    {
        var material = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());
        var file = Write("higher-layer.key", material);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{McpOperatorDelegationOptions.SectionName}:SharedKeyBase64"] = Convert.ToBase64String(new byte[32]),
                ["M2M:Authority"] = "https://netratel.example.invalid",
                ["M2M:Audience"] = "netratel.api",
                ["M2MClients:netratel-mcp-http:Secret"] = "lower-layer-secret"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NETRATEL_MCP_DELEGATION_KEY_FILE"] = file,
                ["M2MClients:netratel-mcp-http:Secret_FILE"] = file
            }).Build();
        var delegation = McpOperatorDelegationOptions.FromConfiguration(config);
        Assert.Equal(string.Empty, delegation.SharedKeyBase64);
        Assert.Equal(file, delegation.SharedKeyBase64File);
        Assert.Equal(material, new M2MDeploymentProfileResolver(config).Resolve("netratel-mcp-http")!.Secret);
    }

    private static McpOperatorDelegationOptions Delegation(string file) => new()
    {
        Enabled = true, Issuer = "netratel-mcp", Audience = "netratel-api",
        KeyId = "independent-delegation-key", SharedKeyBase64File = file
    };

    private string Write(string name, string contents)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, true);
}

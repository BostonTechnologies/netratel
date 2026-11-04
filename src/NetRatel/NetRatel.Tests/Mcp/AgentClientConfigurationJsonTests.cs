using System.Text;
using NetRatel.AgentClient;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class AgentClientConfigurationJsonTests
{
    [Fact]
    public void Configuration_store_preserves_frozen_Web_bytes_and_reads_case_insensitive_BOM_profiles()
    {
        using var files = new ScratchFiles();
        var path = files.Path("profile.json");
        var configuration = new AgentClientConfiguration("https://api.example.test/é?q=<>&", "https://issuer.example.test/token",
            "client", "user@example.test", "synthetic\"password\\\n", "openid profile")
        {
            ApiM2MTokenUrl = "https://api.example.test/m2m", ApiM2MClientId = "machine",
            ApiM2MClientSecret = "synthetic-m2m", ApiM2MScope = "netratel.api",
            IntegrationCredential = "synthetic-integration", AuthenticationMode = "Oidc", ProfileName = "personal",
            Instance = "dev", DefaultTenantId = "007", CredentialPurpose = "Api",
            ApprovedOrigin = "https://api.example.test", ApprovedTarget = "https://api.example.test/é",
            ApprovedTokenEndpoint = "https://issuer.example.test/token",
            IntegrationCredentialFile = "/synthetic/integration.secret", OidcAppPasswordFile = "/synthetic/password.secret",
            ApiM2MClientSecretFile = "/synthetic/m2m.secret"
        };
        new AgentClientConfigurationStore(path).Save(configuration);
        Assert.Equal(FrozenJson.ReplaceLineEndings(Environment.NewLine) + Environment.NewLine, File.ReadAllText(path));
        Assert.Equal(configuration, NetRatelConfigurationService.ReadConfiguration(path));

        var legacy = FrozenJson.Replace("apiBaseUrl", "APIBASEURL", StringComparison.Ordinal)
            .Replace("defaultTenantId", "DEFAULTTENANTID", StringComparison.Ordinal);
        File.WriteAllText(files.Path("legacy.json"), legacy, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(configuration, NetRatelConfigurationService.ReadConfiguration(files.Path("legacy.json")));

        var empty = files.Path("empty.json");
        ConfigurationFileStorage.WriteAtomic(empty, """{"apiBaseUrl":null,"oidcAppPassword":null,"instance":null}""");
        Assert.Equal(AgentClientConfiguration.Empty, NetRatelConfigurationService.ReadConfiguration(empty));
        new AgentClientConfigurationStore(empty).Save(AgentClientConfiguration.Empty);
        Assert.Equal("{}" + Environment.NewLine, File.ReadAllText(empty));
    }

    [Fact]
    public void Generated_configuration_IO_preserves_secret_references_rebind_and_stale_review_guards()
    {
        using var files = new ScratchFiles();
        var path = files.Path("profile.json");
        var configuration = new AgentClientConfiguration("https://api.example.test/", null, null, null, null, null)
        {
            IntegrationCredential = "synthetic-private-credential", DefaultTenantId = "7"
        };
        var service = new NetRatelConfigurationService();
        service.SaveProfile(path, configuration, ConfigurationSurface.StdioMcp);
        var persisted = NetRatelConfigurationService.ReadConfiguration(path);
        Assert.Null(persisted.IntegrationCredential);
        Assert.NotNull(persisted.IntegrationCredentialFile);
        Assert.DoesNotContain("synthetic-private-credential", File.ReadAllText(path));
        Assert.Equal("synthetic-private-credential\n", File.ReadAllText(persisted.IntegrationCredentialFile));
        Assert.Equal("Api", persisted.CredentialPurpose);
        Assert.Equal("https://api.example.test", persisted.ApprovedTarget);
        Assert.True(service.Load(new(ConfigurationSurface.StdioMcp, path, GetEnvironmentVariable: _ => null)).Validate().IsValid);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(persisted.IntegrationCredentialFile));
        }

        var reviewed = ConfigurationFileStorage.Hash(path);
        new AgentClientConfigurationStore(path).Save(persisted with { DefaultTenantId = "8" });
        var saved = File.ReadAllBytes(path);
        var entries = Directory.GetFiles(files.Directory).Order(StringComparer.Ordinal).ToArray();
        Assert.Contains("changed after review", Assert.Throws<AgentClientValidationException>(() =>
            service.SaveProfile(path, configuration, ConfigurationSurface.StdioMcp, replace: true, expectedExistingHash: reviewed)).Message);
        Assert.Contains("reauthorization/rebind", Assert.Throws<AgentClientValidationException>(() =>
            new AgentClientConfigurationStore(path).Save(persisted with { ApiBaseUrl = "https://other.example.test/" })).Message);
        Assert.Equal(saved, File.ReadAllBytes(path));
        Assert.Equal(entries, Directory.GetFiles(files.Directory).Order(StringComparer.Ordinal).ToArray());
    }

    // Frozen from the ordinary reflection-based configuration store before the metadata change.
    private const string FrozenJson = """
        {
          "apiBaseUrl": "https://api.example.test/\u00E9?q=\u003C\u003E\u0026",
          "oidcTokenUrl": "https://issuer.example.test/token",
          "oidcClientId": "client",
          "oidcUsername": "user@example.test",
          "oidcAppPassword": "synthetic\u0022password\\\n",
          "oidcScope": "openid profile",
          "apiM2MTokenUrl": "https://api.example.test/m2m",
          "apiM2MClientId": "machine",
          "apiM2MClientSecret": "synthetic-m2m",
          "apiM2MScope": "netratel.api",
          "integrationCredential": "synthetic-integration",
          "authenticationMode": "Oidc",
          "profileName": "personal",
          "instance": "dev",
          "defaultTenantId": "007",
          "credentialPurpose": "Api",
          "approvedOrigin": "https://api.example.test",
          "approvedTarget": "https://api.example.test/\u00E9",
          "approvedTokenEndpoint": "https://issuer.example.test/token",
          "integrationCredentialFile": "/synthetic/integration.secret",
          "oidcAppPasswordFile": "/synthetic/password.secret",
          "apiM2MClientSecretFile": "/synthetic/m2m.secret"
        }
        """;

    private sealed class ScratchFiles : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("netratel-config-json-").FullName;
        public string Path(string name) => System.IO.Path.Combine(Directory, name);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

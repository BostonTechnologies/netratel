using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using NetRatel.AgentClient;
using NetRatel.Mcp.Http;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class SharedHeadlessConfigurationTests
{
    [Theory]
    [InlineData(ConfigurationSurface.Cli)]
    [InlineData(ConfigurationSurface.StdioMcp)]
    [InlineData(ConfigurationSurface.HttpMcpClient)]
    public void Named_profiles_use_the_configured_persistent_tooling_root_and_explicit_files_keep_precedence(ConfigurationSurface surface)
    {
        using var files = new ScratchFiles();
        string? EnvironmentValue(string key) => key switch
        {
            "NETRATEL_CLIENT_DIRECTORY" => files.Directory,
            "NETRATEL_SETUP_DIRECTORY" => files.Path("separate-host-state"),
            _ => null
        };
        var expected = Path.Combine(files.Directory, "profiles", surface.ToString().ToLowerInvariant(), "personal.json");
        Assert.Equal(expected, NetRatelConfigurationService.ProfilePath(surface, "personal", EnvironmentValue));
        var service = new NetRatelConfigurationService();
        var selected = service.Load(new(surface, ProfileName: "personal", GetEnvironmentVariable: EnvironmentValue), allowCreate: true);
        Assert.Equal(expected, selected.ConfigurationPath);
        HttpHostingStorage.ValidateWritable(Path.GetDirectoryName(expected)!);
        Assert.Empty(System.IO.Directory.EnumerateFiles(Path.GetDirectoryName(expected)!));
        var config = Integration("synthetic-personal-credential") with { ApiBaseUrl = surface == ConfigurationSurface.HttpMcpClient ? "https://selected.example/mcp" : "https://selected.example/" };
        service.SaveProfile(expected, config, surface);
        Assert.True(service.Load(new(surface, ProfileName: "personal", GetEnvironmentVariable: EnvironmentValue)).Validate().IsValid);
        var explicitPath = files.Path("managed.json"); service.SaveProfile(explicitPath, config, surface);
        Assert.Equal(explicitPath, service.Load(new(surface, explicitPath, GetEnvironmentVariable: EnvironmentValue)).ConfigurationPath);
    }

    [Fact]
    public void Container_default_profile_without_a_durable_setup_root_has_one_concrete_remediation()
    {
        var error = Assert.Throws<AgentClientValidationException>(() => NetRatelConfigurationService.ProfilePath(ConfigurationSurface.StdioMcp, "default", key => key == "DOTNET_RUNNING_IN_CONTAINER" ? "true" : null));
        Assert.Contains("docker compose -f compose.setup.yaml run --rm --no-deps mcp-http setup --target stdio", error.Message);
    }

    [Fact]
    public void Environment_only_outbound_uses_dedicated_keys_and_preserves_target_allowlist()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatel_MCP_CONFIG"] = "/legacy-unselected/config.json"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NetRatel:Mcp:Http:Instance"] = "prod",
            ["NetRatel:Mcp:Http:ProdApiBaseUrl"] = "https://selected.example/",
            ["NetRatel:Mcp:Http:ConfigurationPath"] = "",
            ["NETRATEL_MCP_CONFIG"] = "/old-unselected/config.json",
            ["NetRatel:Mcp:Outbound:ApiBaseUrl"] = "https://selected.example/",
            ["NetRatel:Mcp:Outbound:AuthenticationMode"] = "ApiM2MClientSecret",
            ["NetRatel:Mcp:Outbound:ApiM2MTokenUrl"] = "https://selected.example/connect/token",
            ["NetRatel:Mcp:Outbound:ApiM2MClientId"] = "gateway",
            ["NetRatel:Mcp:Outbound:ApiM2MClientSecret"] = "synthetic-outbound-secret",
            ["NetRatel:Mcp:Outbound:ApiM2MScope"] = "netratel.api",
            ["NETRATEL_CLI_API_BASE_URL"] = "https://unrelated.example",
            ["NETRATEL_CLI_INTEGRATION_TOKEN"] = "unrelated-secret"
        }).Build();

        var binding = NetRatelMcpHttpTargetBinding.Resolve(NetRatelMcpHttpOptions.FromConfiguration(configuration));

        binding.Target.ApiBaseUri.Should().Be(new Uri("https://selected.example/"));
        binding.OutboundOptions.ClientId.Should().Be("gateway");
        binding.ConfigurationPath.Should().Be("environment");
        binding.OutboundOptions.ToString().Should().NotContain("synthetic-outbound-secret");
    }

    [Fact]
    public void Explicit_empty_canonical_mcp_path_clears_case_sensitive_legacy_alias()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["NETRATEL_MCP_CONFIG"] = string.Empty,
            ["NetRatel_MCP_CONFIG"] = "/legacy-unselected/config.json"
        };

        AgentClientConfigurationResolver.GetMcpConfigurationPath(name => variables.GetValueOrDefault(name)).Should().BeNull();
    }

    [Fact]
    public void Http_arrays_replace_in_each_provider_including_explicit_empty_array()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatel:Mcp:Http:RequiredScopes:0"] = "old-one",
                ["NetRatel:Mcp:Http:RequiredScopes:1"] = "old-two",
                ["NetRatel:Mcp:Http:AllowedOrigins:0"] = "https://old.example"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatel:Mcp:Http:RequiredScopes:0"] = "new-one",
                ["NetRatel:Mcp:Http:AllowedOrigins"] = null
            }).Build();

        var options = NetRatelMcpHttpOptions.FromConfiguration(configuration);

        options.RequiredScopes.Should().Equal("new-one");
        options.AllowedOrigins.Should().BeEmpty();
        var gap = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NetRatel:Mcp:Http:RequiredScopes:1"] = "invalid-gap"
        }).Build();
        gap.Invoking(value => NetRatelMcpHttpOptions.FromConfiguration(value)).Should().Throw<AgentClientValidationException>().WithMessage("*consecutive indices*");
    }

    [Fact]
    public void Dedicated_environment_file_replaces_lower_typed_json_inline_secret_with_accurate_source()
    {
        using var files = new ScratchFiles();
        var secretPath = files.Path("selected.secret");
        ConfigurationFileStorage.WriteAtomic(secretPath, "higher-synthetic-secret\n");
        var prefix = "NETRATEL_CONFIG_TEST_" + Guid.NewGuid().ToString("N") + "_";
        var key = prefix + "NetRatel__Mcp__Outbound__IntegrationCredentialFile";
        try
        {
            Environment.SetEnvironmentVariable(key, secretPath);
            using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""{"NetRatel":{"Mcp":{"Outbound":{"ApiBaseUrl":"https://selected.example/","IntegrationCredential":"lower-synthetic-secret"}}}}"""));
            var configuration = new ConfigurationBuilder().AddJsonStream(json).AddEnvironmentVariables(prefix).Build();
            var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.StdioMcp), configuration);

            effective.Validate().IsValid.Should().BeTrue();
            effective.Configuration.IntegrationCredential.Should().Be("higher-synthetic-secret");
            effective.Sources["IntegrationCredential"].Source.Should().Contain("EnvironmentVariablesConfigurationProvider");
            effective.Sources["IntegrationCredential"].ExternallyManaged.Should().BeTrue();
            effective.Sources["IntegrationCredential"].Reference.Should().Be(secretPath);
            JsonSerializer.Serialize(effective.Diagnostics()).Should().NotContain("lower-synthetic-secret").And.NotContain("higher-synthetic-secret");
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public void Higher_environment_alias_beats_lower_json_canonical_and_canonical_wins_within_environment()
    {
        var prefix = "NETRATEL_CONFIG_TEST_" + Guid.NewGuid().ToString("N") + "_";
        var alias = prefix + "NETRATEL_MCP_OUTBOUND_API_BASE_URL";
        var canonical = prefix + "NetRatel__Mcp__Outbound__ApiBaseUrl";
        var instance = prefix + "NETRATEL_MCP_INSTANCE";
        try
        {
            Environment.SetEnvironmentVariable(alias, "https://alias-env.example/");
            Environment.SetEnvironmentVariable(instance, "prod");
            const string json = """{"NetRatel":{"Mcp":{"Outbound":{"ApiBaseUrl":"https://canonical-json.example/"},"Http":{"Instance":"dev"}}}}""";
            IConfigurationRoot Build() => new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).AddEnvironmentVariables(prefix).Build();
            var configuration = Build();
            var service = new NetRatelConfigurationService();
            service.Load(new(ConfigurationSurface.StdioMcp), configuration).Configuration.ApiBaseUrl.Should().Be("https://alias-env.example/");
            NetRatelMcpHttpOptions.FromConfiguration(configuration).Instance.Should().Be("prod");
            Environment.SetEnvironmentVariable(canonical, "https://canonical-env.example/");
            service.Load(new(ConfigurationSurface.StdioMcp), Build()).Configuration.ApiBaseUrl.Should().Be("https://canonical-env.example/");
        }
        finally
        {
            Environment.SetEnvironmentVariable(alias, null);
            Environment.SetEnvironmentVariable(canonical, null);
            Environment.SetEnvironmentVariable(instance, null);
        }
    }

    [Fact]
    public void Mcp_never_imports_cli_or_agent_environment_credentials()
    {
        var config = new NetRatelConfigurationService().Load(new(ConfigurationSurface.StdioMcp,
            GetEnvironmentVariable: name => name switch
            {
                "NETRATEL_CLI_API_BASE_URL" => "https://unrelated.example",
                "NETRATEL_INTEGRATION_TOKEN" => "unrelated-secret",
                "NETRATEL_AGENT_CLIENT_OIDC_APP_PASSWORD" => "unrelated-secret",
                _ => null
            }));

        config.Configuration.Should().Be(AgentClientConfiguration.Empty);
        config.Validate().IsValid.Should().BeFalse();
    }

    [Fact]
    public void Canonical_keys_win_aliases_and_empty_values_clear_without_fallback()
    {
        var variables = new Dictionary<string, string?>
        {
            ["NetRatel__Mcp__Outbound__ApiBaseUrl"] = "https://canonical.example",
            ["NETRATEL_MCP_OUTBOUND_API_BASE_URL"] = "https://alias.example",
            ["NetRatel__Mcp__Outbound__IntegrationCredential"] = "",
            ["NETRATEL_MCP_OUTBOUND_INTEGRATION_TOKEN"] = "alias-secret"
        };
        var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.StdioMcp,
            GetEnvironmentVariable: name => variables.GetValueOrDefault(name)));

        effective.Configuration.ApiBaseUrl.Should().Be("https://canonical.example");
        effective.Configuration.IntegrationCredential.Should().BeEmpty();
        effective.Sources["ApiBaseUrl"].Source.Should().Be("NetRatel__Mcp__Outbound__ApiBaseUrl");
        effective.Sources["ApiBaseUrl"].ExternallyManaged.Should().BeTrue();
    }

    [Fact]
    public void Explicit_missing_file_does_not_use_environment_credentials()
    {
        var load = () => new NetRatelConfigurationService().Load(new(ConfigurationSurface.StdioMcp,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            GetEnvironmentVariable: name => name == "NETRATEL_MCP_OUTBOUND_INTEGRATION_TOKEN" ? "synthetic" : null));

        load.Should().Throw<AgentClientValidationException>().WithMessage("*explicitly selected*does not exist*");
    }

    [Theory]
    [InlineData(true, "ApiBaseUrl must be an absolute HTTPS URL")]
    [InlineData(false, "OidcTokenUrl must be an absolute HTTPS URL")]
    public void Unchanged_malformed_endpoint_reports_syntax_error_and_a_different_endpoint_requires_reauthorization(bool malformedApi, string expectedError)
    {
        using var files = new ScratchFiles();
        var path = files.Path("invalid-endpoint.json");
        var configuration = new AgentClientConfiguration("https://selected.example/", "https://issuer.example/token",
            "application", "service", "synthetic-private-password", null);
        configuration = malformedApi
            ? configuration with { ApiBaseUrl = "not-an-absolute-uri" }
            : configuration with { OidcTokenUrl = "not-an-absolute-uri" };
        new AgentClientConfigurationStore(path).Save(configuration);
        var service = new NetRatelConfigurationService();

        var effective = service.Load(new(ConfigurationSurface.StdioMcp, path, GetEnvironmentVariable: _ => null));
        var validation = effective.Validate();

        validation.IsValid.Should().BeFalse();
        validation.Errors.Should().ContainSingle().Which.Should().StartWith(expectedError);
        validation.Errors.Should().NotContain(error => error.Contains("synthetic-private-password", StringComparison.Ordinal));
        effective.Configuration.OidcAppPassword.Should().Be("synthetic-private-password");

        var changed = malformedApi
            ? AgentClientConfiguration.Empty with { ApiBaseUrl = "https://different.example/" }
            : AgentClientConfiguration.Empty with { OidcTokenUrl = "https://different-issuer.example/token" };
        var loadChanged = () => service.Load(new(ConfigurationSurface.StdioMcp, path,
            CommandOptions: changed, GetEnvironmentVariable: _ => null));
        loadChanged.Should().Throw<AgentClientValidationException>().WithMessage("*reauthorization/rebind*");
    }

    [Fact]
    public void Higher_environment_secret_file_replaces_lower_inline_secret_and_diagnostics_redact()
    {
        using var files = new ScratchFiles();
        var path = files.Path("profile.json");
        var secretPath = files.Path("credential.secret");
        ConfigurationFileStorage.WriteAtomic(secretPath, "new-synthetic-secret\n");
        new AgentClientConfigurationStore(path).Save(Integration("old-synthetic-secret"));
        var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.StdioMcp, path,
            GetEnvironmentVariable: name => name == "NETRATEL_MCP_OUTBOUND_INTEGRATION_TOKEN_FILE" ? secretPath : null));

        effective.Configuration.IntegrationCredential.Should().Be("new-synthetic-secret");
        effective.Validate().IsValid.Should().BeTrue();
        var json = JsonSerializer.Serialize(effective.Diagnostics());
        json.Should().NotContain("new-synthetic-secret").And.NotContain("old-synthetic-secret");
        json.Should().Contain("***REDACTED***");
        effective.Configuration.ToString().Should().NotContain("new-synthetic-secret");
        effective.Configuration.Resolve().ToString().Should().NotContain("new-synthetic-secret");
    }

    [Fact]
    public void Competing_secret_sources_and_conflicting_auth_modes_are_actionable_errors()
    {
        var load = () => new NetRatelConfigurationService().Load(new(ConfigurationSurface.StdioMcp,
            GetEnvironmentVariable: name => name switch
            {
                "NETRATEL_MCP_OUTBOUND_INTEGRATION_TOKEN" => "synthetic",
                "NETRATEL_MCP_OUTBOUND_INTEGRATION_TOKEN_FILE" => "/not-needed/synthetic.secret",
                _ => null
            }));
        load.Should().Throw<AgentClientValidationException>().WithMessage("*competing inline and file*");

        var conflicting = Integration("synthetic") with { AuthenticationMode = "Oidc" };
        new AgentClientConfigurationValidator().Validate(null, conflicting).Failures.Should().Contain(error => error.Contains("conflicts"));
        var modes = Integration("synthetic") with { OidcScope = "netratel.api" };
        modes.Invoking(value => value.Resolve()).Should().Throw<AgentClientValidationException>().WithMessage("*exactly one authentication mode*");
    }

    [Fact]
    public void Explicit_nonsecret_command_option_wins_and_missing_options_leave_lower_values()
    {
        using var files = new ScratchFiles();
        var path = files.Path("profile.json");
        new AgentClientConfigurationStore(path).Save(Integration("synthetic") with { Instance = "dev" });
        var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.StdioMcp, path,
            CommandOptions: AgentClientConfiguration.Empty with { Instance = "prod" },
            GetEnvironmentVariable: name => name == "NETRATEL_MCP_OUTBOUND_INSTANCE" ? "dev" : null));

        effective.Configuration.Instance.Should().Be("prod");
        effective.Configuration.ApiBaseUrl.Should().Be("https://selected.example/");
        effective.Sources["Instance"].Source.Should().Be("command");
        effective.Sources["ApiBaseUrl"].Source.Should().StartWith("file:");
    }

    [Fact]
    public void Bound_profile_rejects_target_edits_purpose_switching_and_stale_review()
    {
        using var files = new ScratchFiles();
        var path = files.Path("profile.json");
        var service = new NetRatelConfigurationService();
        service.SaveProfile(path, Integration("synthetic"), ConfigurationSurface.StdioMcp);
        var before = ConfigurationFileStorage.Hash(path);
        var edited = () => service.Load(new(ConfigurationSurface.StdioMcp, path,
            CommandOptions: AgentClientConfiguration.Empty with { ApiBaseUrl = "https://different.example" },
            GetEnvironmentVariable: _ => null));
        edited.Should().Throw<AgentClientValidationException>().WithMessage("*approved target*");
        var purpose = () => service.Load(new(ConfigurationSurface.HttpMcpClient, path, GetEnvironmentVariable: _ => null));
        purpose.Should().Throw<AgentClientValidationException>().WithMessage("*purpose*");
        var persisted = NetRatelConfigurationService.ReadConfiguration(path);
        new AgentClientConfigurationStore(path).Save(persisted with { DefaultTenantId = "7" });
        var stale = () => service.SaveProfile(path, Integration("next-synthetic"), ConfigurationSurface.StdioMcp,
            replace: true, expectedExistingHash: before);
        stale.Should().Throw<AgentClientValidationException>().WithMessage("*changed after review*");
    }

    [Fact]
    public void Saving_profile_stores_owner_only_references_and_cancel_does_not_touch_existing_profile()
    {
        using var files = new ScratchFiles();
        var path = files.Path("profile.json");
        var service = new NetRatelConfigurationService();
        service.SaveProfile(path, Integration("synthetic-private-value"), ConfigurationSurface.Cli);
        var persisted = NetRatelConfigurationService.ReadConfiguration(path);
        File.ReadAllText(path).Should().NotContain("synthetic-private-value");
        persisted.IntegrationCredential.Should().BeNull();
        persisted.IntegrationCredentialFile.Should().NotBeNull();
        ConfigurationFileStorage.ReadText(persisted.IntegrationCredentialFile!).Should().Be("synthetic-private-value\n");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(persisted.IntegrationCredentialFile!).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var before = ConfigurationFileStorage.Hash(path);
        _ = service.Load(new(ConfigurationSurface.Cli, path, GetEnvironmentVariable: _ => null)); // Open and cancel without Save.
        ConfigurationFileStorage.Hash(path).Should().Be(before);
        Directory.GetFiles(files.Directory, "*.lock").Should().BeEmpty();
        var overwrite = () => service.SaveProfile(path, Integration("different-private-value"), ConfigurationSurface.Cli);
        overwrite.Should().Throw<AgentClientValidationException>().WithMessage("*already exists*");
        ConfigurationFileStorage.Hash(path).Should().Be(before);
    }

    [Fact]
    public void Review_of_absent_profile_fences_another_creation_before_save()
    {
        using var files = new ScratchFiles();
        var path = files.Path("profile.json");
        var service = new NetRatelConfigurationService();
        service.SaveProfile(path, Integration("first-synthetic"), ConfigurationSurface.Cli, expectedExistingHash: "");
        var before = ConfigurationFileStorage.Hash(path);
        var concurrent = () => service.SaveProfile(path, Integration("second-synthetic"), ConfigurationSurface.Cli,
            replace: true, expectedExistingHash: "");
        concurrent.Should().Throw<AgentClientValidationException>().WithMessage("*changed after review*");
        ConfigurationFileStorage.Hash(path).Should().Be(before);
    }

    [Theory]
    [InlineData("{\"apiBaseUrl\":\"https://a.example\",\"ApiBaseUrl\":\"https://b.example\"}", "*duplicate*")]
    [InlineData("{\"unknownSetting\":\"synthetic-private-value\"}", "*unsupported*")]
    [InlineData("{\"apiBaseUrl\": [\"synthetic-private-value\"]}", "*not a valid*")]
    public void Invalid_file_schema_never_echoes_values(string json, string error)
    {
        using var files = new ScratchFiles();
        var path = files.Path("profile.json");
        ConfigurationFileStorage.WriteAtomic(path, json);
        var read = () => NetRatelConfigurationService.ReadConfiguration(path);
        read.Should().Throw<AgentClientValidationException>().WithMessage(error).Which.Message.Should().NotContain("synthetic-private-value");
    }

    [Fact]
    public void Secret_reads_are_bounded_symlink_safe_and_readonly_inputs_are_managed()
    {
        using var files = new ScratchFiles();
        var path = files.Path("profile.json");
        var secretPath = files.Path("oversized.secret");
        ConfigurationFileStorage.WriteAtomic(secretPath, new string('x', 16_385));
        var oversized = () => NetRatelConfigurationService.ResolveSecretReferences(Integration(null) with { IntegrationCredentialFile = secretPath });
        oversized.Should().Throw<AgentClientValidationException>().WithMessage("*exceeds*");
        new AgentClientConfigurationStore(path).Save(Integration("synthetic"));
        if (!OperatingSystem.IsWindows())
        {
            var link = files.Path("symlink.json");
            File.CreateSymbolicLink(link, path);
            var read = () => ConfigurationFileStorage.ReadText(link);
            read.Should().Throw<AgentClientValidationException>().WithMessage("*symbolic links*");
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
            var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.Cli, path, GetEnvironmentVariable: _ => null));
            effective.Sources["ApiBaseUrl"].ExternallyManaged.Should().BeTrue();
        }
    }

    [Fact]
    public void Independent_profile_files_do_not_mix_identities_and_token_endpoint_edits_fail()
    {
        using var files = new ScratchFiles();
        var service = new NetRatelConfigurationService();
        var first = files.Path("first.json");
        var second = files.Path("second.json");
        service.SaveProfile(first, Integration("first-synthetic"), ConfigurationSurface.StdioMcp);
        service.SaveProfile(second, Integration("second-synthetic") with { ApiBaseUrl = "https://second.example/" }, ConfigurationSurface.StdioMcp);
        service.Load(new(ConfigurationSurface.StdioMcp, first, GetEnvironmentVariable: _ => null)).Configuration.IntegrationCredential.Should().Be("first-synthetic");
        service.Load(new(ConfigurationSurface.StdioMcp, second, GetEnvironmentVariable: _ => null)).Configuration.IntegrationCredential.Should().Be("second-synthetic");
        var oidc = files.Path("oidc.json");
        service.SaveProfile(oidc, new("https://selected.example/", "https://issuer.example/token", "application", "service", "private-synthetic", null), ConfigurationSurface.StdioMcp);
        var edit = () => service.Load(new(ConfigurationSurface.StdioMcp, oidc,
            CommandOptions: AgentClientConfiguration.Empty with { OidcTokenUrl = "https://other-issuer.example/token" }, GetEnvironmentVariable: _ => null));
        edit.Should().Throw<AgentClientValidationException>().WithMessage("*token endpoint*reauthorization*");
    }

    [Fact]
    public async Task Doctor_defaults_to_local_validation_and_unauthorized_connectivity_is_failure_without_fallback()
    {
        var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.StdioMcp,
            GetEnvironmentVariable: name => name switch
            {
                "NETRATEL_MCP_OUTBOUND_API_BASE_URL" => "https://selected.example/",
                "NETRATEL_MCP_OUTBOUND_INTEGRATION_TOKEN" => "synthetic-private-value",
                _ => null
            }));
        var handler = new UnauthorizedHandler();
        var doctor = new NetRatelDoctorService(() => handler);
        var local = await doctor.CheckAsync(effective);
        local.IsValid.Should().BeTrue();
        handler.Calls.Should().Be(0);
        var connected = await doctor.CheckAsync(effective, connectivity: true);
        connected.IsValid.Should().BeFalse();
        connected.ExitCode.Should().NotBe(0);
        connected.Checks.Single(check => check.Name == "authentication").HttpStatus.Should().Be(401);
        handler.Calls.Should().Be(1);
        JsonSerializer.Serialize(connected).Should().NotContain("synthetic-private-value");
    }

    [Theory]
    [InlineData("<html><title>NetRatel</title></html>", false)]
    [InlineData("{\"authenticated\":true}", false)]
    [InlineData("{\"authenticated\":false,\"authMode\":\"integration_credential\",\"groups\":[],\"roles\":[]}", false)]
    [InlineData("{\"authenticated\":true,\"authMode\":\"integration_credential\",\"groups\":[],\"roles\":[]}", true)]
    public async Task Doctor_requires_the_actual_authenticated_status_document(string body, bool valid)
    {
        var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.StdioMcp,
            GetEnvironmentVariable: name => name switch
            {
                "NETRATEL_MCP_OUTBOUND_API_BASE_URL" => "https://selected.example/",
                "NETRATEL_MCP_OUTBOUND_INTEGRATION_TOKEN" => "synthetic",
                _ => null
            }));
        var result = await new NetRatelDoctorService(() => new StatusHandler(body)).CheckAsync(effective, connectivity: true);
        result.IsValid.Should().Be(valid);
    }

    private sealed class StatusHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private sealed class UnauthorizedHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsolutePath.Should().Be(NetRatelDoctorService.AuthenticationStatusPath);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        }
    }

    private static AgentClientConfiguration Integration(string? secret) => new("https://selected.example/", null, null, null, null, null)
        { IntegrationCredential = secret };

    private sealed class ScratchFiles : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("netratel-config-").FullName;
        public string Path(string name) => System.IO.Path.Combine(Directory, name);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

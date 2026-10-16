using System.Text.Json;
using NetRatel.AgentClient;
using NetRatel.Mcp.Http;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class HttpHostingEffectiveDeploymentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netratel-effective-" + Guid.NewGuid().ToString("N"));

    private HttpHostingSetupRequest Request => new(_directory, "https://web.example.invalid", "https://api.example.invalid");

    private HttpHostingEffectiveDeployment Source(Dictionary<string, string?>? api = null, Dictionary<string, string?>? mcp = null,
        Dictionary<string, string?>? image = null, Dictionary<string, string?>? protectedConfiguration = null, string? imageId = null)
    {
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(_directory, "effective.json");
        var content = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["services"] = new Dictionary<string, object>
            {
                ["web"] = new { environment = new Dictionary<string, string?>() },
                ["api"] = new { environment = api ?? [] },
                ["mcp-http"] = new { environment = mcp ?? [] }
            },
            ["x-netratel-image-environment"] = new Dictionary<string, object> { ["mcp-http"] = image ?? [] },
            ["x-netratel-protected-configuration"] = new Dictionary<string, object> { ["mcp-http"] = protectedConfiguration ?? [] },
            ["x-netratel-image-ids"] = imageId is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["mcp-http"] = imageId }
        });
        ConfigurationFileStorage.WriteAtomic(path, content, replace: File.Exists(path));
        return HttpHostingEffectiveDeployment.Load(path);
    }

    private static Dictionary<string, string> Mcp(bool local = true) => new()
    {
        ["NetRatel__Mcp__Http__Instance"] = "prod",
        ["NetRatel__Mcp__Http__ProdApiBaseUrl"] = "https://api.example.invalid",
        ["NetRatel__Mcp__Http__PublicResourceUri"] = "https://web.example.invalid/mcp",
        ["NetRatel__Mcp__Http__LocalCredentialMode"] = local ? "true" : "false",
        ["NetRatel__Mcp__Delegation__ServicePrincipal"] = "netratel-mcp-http"
    };

    private static Dictionary<string, string> Api(bool local = true) => new()
    {
        ["Integration__HttpMcp__AuthenticationMode"] = local ? "LocalCredential" : "Oidc",
        ["NetRatel__Mcp__Delegation__ServicePrincipal"] = "netratel-mcp-http"
    };

    [Fact]
    public void Caller_repair_preserves_effective_occupied_indices_and_appends_at_an_available_index()
    {
        var source = Source(api: new()
        {
            // docker compose config has already resolved inline index 0 masking
            // the env_file index. The missing MCP identity must be additive.
            ["M2M__AllowedCallerClientIds__0"] = "other.worker",
            ["M2M__AllowedCallerClientIds__1"] = "netratel.cli"
        });
        var api = Api();
        api["M2M__AllowedCallerClientIds__0"] = "netratel-mcp-http";
        source.Prepare(Request with { ExistingM2MCallerClientIds = ["other.worker", "netratel.cli"] }, [], api, Mcp());
        Assert.False(api.ContainsKey("M2M__AllowedCallerClientIds__0"));
        Assert.False(api.ContainsKey("M2M__AllowedCallerClientIds__1"));
        Assert.Equal("netratel-mcp-http", api["M2M__AllowedCallerClientIds__2"]);
        Assert.Contains(source.Changes, change => change.Contains("mask matching env_file indices", StringComparison.Ordinal) || change.Contains("mask", StringComparison.Ordinal));
    }

    [Fact]
    public void Identity_mismatch_has_one_redacted_actionable_diagnostic_and_does_not_change_source_bytes()
    {
        const string secret = "do-not-print-this-credential";
        var source = Source(mcp: new()
        {
            ["NetRatel__Mcp__Delegation__ServicePrincipal"] = "delegation-service",
            ["NetRatel__Mcp__Outbound__ApiM2MClientId"] = "outbound-service",
            ["NetRatel__Mcp__Outbound__ApiM2MClientSecret"] = secret
        });
        var before = File.ReadAllBytes(source.SourcePath);
        var exception = Assert.Throws<AgentClientValidationException>(() => source.Prepare(Request, [], Api(), Mcp()));
        Assert.StartsWith("outbound_identity_mismatch:", exception.Message);
        Assert.Contains("winning source", exception.Message);
        Assert.Contains("NetRatel:Mcp:Outbound:ApiM2MClientId", exception.Message);
        Assert.DoesNotContain(secret, exception.Message);
        Assert.Equal(before, File.ReadAllBytes(source.SourcePath));
        Assert.False(Directory.Exists(Path.Combine(_directory, "secrets")));
    }

    [Theory]
    [InlineData("Production", "prod", true)]
    [InlineData("Development", "dev", true)]
    [InlineData("Production", "dev", false)]
    [InlineData("Staging", "prod", false)]
    public void Api_environment_is_authoritative_without_rewriting_it(string environment, string instance, bool valid)
    {
        var source = Source(api: new() { ["DOTNET_ENVIRONMENT"] = environment });
        var request = Request with { Instance = instance };
        var mcp = Mcp();
        mcp["NetRatel__Mcp__Http__Instance"] = instance;
        if (instance == "dev")
        {
            mcp.Remove("NetRatel__Mcp__Http__ProdApiBaseUrl");
            mcp["NetRatel__Mcp__Http__DevApiBaseUrl"] = request.ApiBaseUrl;
        }
        var api = Api();
        if (valid) source.Prepare(request, [], api, mcp);
        else Assert.StartsWith("api_instance_mismatch:", Assert.Throws<AgentClientValidationException>(() => source.Prepare(request, [], api, mcp)).Message);
        Assert.False(api.ContainsKey("DOTNET_ENVIRONMENT"));
        Assert.False(api.ContainsKey("ASPNETCORE_ENVIRONMENT"));
    }

    [Fact]
    public void Reviewed_inactive_clear_suppresses_both_canonical_and_alias_image_fallbacks()
    {
        var source = Source(mcp: new() { ["NETRATEL_MCP_DEV_API_BASE_URL"] = "https://api.example.invalid/" }, image: new()
        {
            ["NetRatel__Mcp__Http__DevApiBaseUrl"] = "https://api.example.invalid",
            ["NetRatel__Mcp__Http__ProdApiBaseUrl"] = "https://old.example.invalid"
        });
        var mcp = Mcp();
        source.Prepare(Request, [], Api(), mcp);
        Assert.Equal("", mcp["NetRatel__Mcp__Http__DevApiBaseUrl"]);
        Assert.Equal("", mcp["NETRATEL_MCP_DEV_API_BASE_URL"]);
        Assert.Equal(Request.ApiBaseUrl, mcp["NetRatel__Mcp__Http__ProdApiBaseUrl"]);
        Assert.Contains(source.Changes, change => change.StartsWith("Clear NetRatel:Mcp:Http:DevApiBaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Explicit_empty_composed_value_suppresses_image_default_and_serving_validator_still_rejects_invalid_inactive_endpoint()
    {
        var source = Source(mcp: new() { ["NetRatel__Mcp__Http__DevApiBaseUrl"] = "" }, image: new()
        {
            ["NetRatel__Mcp__Http__DevApiBaseUrl"] = "http://invalid.example.invalid"
        });
        source.Prepare(Request, [], Api(), Mcp());
        var inherited = Source(image: new() { ["NetRatel__Mcp__Http__DevApiBaseUrl"] = "http://invalid.example.invalid" });
        Assert.StartsWith("serving_options_invalid:", Assert.Throws<AgentClientValidationException>(() => inherited.Prepare(Request, [], Api(), Mcp())).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unset_removes_image_environment_and_allows_protected_provider_fallback_while_empty_suppresses_it(bool unset)
    {
        var source = Source(mcp: new() { ["NetRatel__Mcp__Http__DevApiBaseUrl"] = unset ? null : "" },
            image: new() { ["NetRatel__Mcp__Http__DevApiBaseUrl"] = "https://unused.example.invalid" },
            protectedConfiguration: new() { ["NetRatel:Mcp:Http:DevApiBaseUrl"] = "http://invalid-protected.example.invalid" });
        if (unset)
            Assert.StartsWith("serving_options_invalid:", Assert.Throws<AgentClientValidationException>(() => source.Prepare(Request, [], Api(), Mcp())).Message);
        else source.Prepare(Request, [], Api(), Mcp());
    }

    [Fact]
    public void Supported_non_local_authentication_uses_the_actual_serving_validator()
    {
        var source = Source();
        var mcp = Mcp(local: false);
        mcp["NetRatel__Mcp__Http__Authority"] = "https://identity.example.invalid";
        mcp["NetRatel__Mcp__Http__Audience"] = "https://web.example.invalid/mcp";
        mcp["NetRatel__Mcp__Http__RequiredGroups__0"] = "operators";
        mcp["NetRatel__Mcp__Http__RequiredScopes__0"] = "netratel.mcp.read";
        source.Prepare(Request with { LocalCredentialMode = false }, [], Api(local: false), mcp);
        mcp["NetRatel__Mcp__Http__ReadScope"] = "invalid scope";
        var actual = new NetRatelMcpHttpOptionsValidator().Validate(null, new NetRatelMcpHttpOptions
        {
            Instance = "prod", ProdApiBaseUrl = Request.ApiBaseUrl, PublicResourceUri = Request.PublicOrigin + "/mcp",
            LocalCredentialMode = false, Authority = "https://identity.example.invalid", Audience = Request.PublicOrigin + "/mcp",
            RequiredGroups = ["operators"], RequiredScopes = ["netratel.mcp.read"], ReadScope = "invalid scope"
        });
        Assert.True(actual.Failed);
        var exception = Assert.Throws<AgentClientValidationException>(() => source.ValidateProposed(Request, new Dictionary<string, string>(), Api(local: false), mcp));
        Assert.Contains(actual.Failures.Single(), exception.Message);
        Assert.StartsWith("http_authentication_mode_mismatch:", Assert.Throws<AgentClientValidationException>(() => source.ValidateProposed(Request, new Dictionary<string, string>(), Api(local: false), Mcp())).Message);
    }

    [Fact]
    public void Runtime_verification_requires_the_saved_proposal_and_the_recreated_serving_values()
    {
        var source = Source();
        var mcp = Mcp();
        var api = Api();
        source.Prepare(Request, [], api, mcp);
        ConfigurationFileStorage.WriteAtomic(Path.Combine(_directory, ".effective-proposal.json"), source.RedactedProposal(Request, new Dictionary<string, string>(), api, mcp));
        string? Environment(string key) => mcp.GetValueOrDefault(key);
        HttpHostingEffectiveDeployment.ValidateRuntimeProposal(_directory, Environment);
        mcp["NetRatel__Mcp__Http__ProdApiBaseUrl"] = "https://stale.example.invalid";
        Assert.StartsWith("effective_runtime_mismatch:", Assert.Throws<AgentClientValidationException>(() => HttpHostingEffectiveDeployment.ValidateRuntimeProposal(_directory, Environment)).Message);
    }

    [Fact]
    public void Changed_complete_source_invalidates_administrator_approval_without_changing_identity_bytes()
    {
        var source = Source();
        var request = Request with { EffectiveComposeSourcePath = source.SourcePath };
        var service = new NetRatelHttpHostingSetupService();
        var fingerprint = service.PrepareRegistration(request);
        var secretPath = Path.Combine(_directory, "secrets", "outbound-m2m.secret");
        var before = File.ReadAllBytes(secretPath);
        Source(api: new() { ["DOTNET_ENVIRONMENT"] = "Production", ["DeploymentRevision"] = "changed-after-approval" });
        var exception = Assert.Throws<AgentClientValidationException>(() => service.Apply(request with { ApprovedRegistrationFingerprint = fingerprint }));
        Assert.Contains("changed after administrator approval", exception.Message);
        Assert.Equal(before, File.ReadAllBytes(secretPath));
        Assert.False(File.Exists(Path.Combine(_directory, "api.env")));
    }

    [Fact]
    public void Approval_without_complete_deployment_source_fails_before_creating_a_generation()
    {
        var exception = Assert.Throws<AgentClientValidationException>(() => new NetRatelHttpHostingSetupService().PrepareRegistration(Request with { EffectiveComposeSourcePath = "" }));
        Assert.StartsWith("deployment_source_unresolved:", exception.Message);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Complete_sources_over_the_native_json_limit_remain_bounded_and_source_bound()
    {
        var source = Source();
        var original = File.ReadAllText(source.SourcePath);
        var expanded = original[..^1] + ",\"x-fixture-padding\":\"" + new string('x', 70_000) + "\"}";
        ConfigurationFileStorage.WriteAtomic(source.SourcePath, expanded, replace: true, maximumBytes: HttpHostingComposeDocument.MaximumSourceBytes);
        var request = Request with { EffectiveComposeSourcePath = source.SourcePath };
        var service = new NetRatelHttpHostingSetupService();
        var approved = request with { ApprovedRegistrationFingerprint = service.PrepareRegistration(request) };
        service.Preview(approved);
        ConfigurationFileStorage.WriteAtomic(source.SourcePath, expanded.Replace("xxxxx", "yyyyy", StringComparison.Ordinal), replace: true,
            maximumBytes: HttpHostingComposeDocument.MaximumSourceBytes);
        Assert.Contains("changed since review", Assert.Throws<AgentClientValidationException>(() => service.Apply(approved)).Message);
    }

    [Fact]
    public void Protected_outbound_and_delegation_providers_are_adopted_without_replacing_credential_bytes()
    {
        const string secret = "existing-protected-outbound-secret";
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var source = Source(protectedConfiguration: new()
        {
            ["NetRatel:Mcp:Http:Instance"] = "prod", ["NetRatel:Mcp:Http:ProdApiBaseUrl"] = Request.ApiBaseUrl,
            ["NetRatel:Mcp:Outbound:AuthenticationMode"] = "ApiM2MClientSecret", ["NetRatel:Mcp:Outbound:ApiBaseUrl"] = Request.ApiBaseUrl,
            ["NetRatel:Mcp:Outbound:ApiM2MTokenUrl"] = Request.ApiBaseUrl + "/connect/token",
            ["NetRatel:Mcp:Outbound:ApiM2MClientId"] = "netratel-mcp-http", ["NetRatel:Mcp:Outbound:ApiM2MClientSecret"] = secret,
            ["NetRatel:Mcp:Outbound:ApiM2MScope"] = "netratel.api", ["NetRatel:Mcp:Delegation:Enabled"] = "true",
            ["NetRatel:Mcp:Delegation:ServicePrincipal"] = "netratel-mcp-http", ["NetRatel:Mcp:Delegation:KeyId"] = "existing-key",
            ["NetRatel:Mcp:Delegation:Issuer"] = "existing-issuer", ["NetRatel:Mcp:Delegation:Audience"] = "existing-audience",
            ["NetRatel:Mcp:Delegation:SharedKeyBase64"] = key
        });
        var before = File.ReadAllBytes(source.SourcePath);
        var existing = ExistingHttpHostSettings.Discover(name => name == HttpHostingEffectiveDeployment.SourceEnvironmentVariable ? source.SourcePath : null);
        Assert.True(existing.HasOutboundIdentity);
        Assert.True(existing.HasDelegationKey);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(existing));
        var output = Path.Combine(_directory, "adopted-setup");
        Assert.False(Directory.Exists(output));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(output);
        else Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var outbound = existing.MaterializeOutboundConfiguration(output);
        var delegation = existing.MaterializeDelegationKey(output);
        Assert.Equal(secret, NetRatelConfigurationService.ReadConfiguration(outbound!).ApiM2MClientSecret);
        Assert.Equal(key, File.ReadAllText(delegation!).Trim());
        var request = Request with { OutputDirectory = output, ExistingOutboundConfigurationPath = outbound, ExistingDelegationKeyFile = delegation,
            DelegationServicePrincipal = existing.DelegationServicePrincipal, DelegationKeyId = existing.DelegationKeyId,
            DelegationIssuer = existing.DelegationIssuer, DelegationAudience = existing.DelegationAudience, EffectiveComposeSourcePath = source.SourcePath };
        new NetRatelHttpHostingSetupService().PrepareRegistration(request);
        Assert.False(File.Exists(Path.Combine(output, "secrets", "outbound-m2m.secret")));
        Assert.False(File.Exists(Path.Combine(output, "secrets", "delegation.key")));
        Assert.Equal(before, File.ReadAllBytes(source.SourcePath));
    }

    [Fact]
    public void Resume_reuses_the_same_original_setup_generation_after_it_becomes_the_serving_source()
    {
        var callers = new Dictionary<string, string?> { ["M2M__AllowedCallerClientIds__0"] = "netratel.cli", ["M2M__AllowedCallerClientIds__1"] = "netratel.client.artifacts" };
        var source = Source(api: callers);
        var request = Request with { EffectiveComposeSourcePath = source.SourcePath };
        new NetRatelHttpHostingSetupService().Apply(request);
        var outbound = Path.Combine(_directory, "secrets", "outbound-m2m.secret");
        var delegation = Path.Combine(_directory, "secrets", "delegation.key");
        var before = File.ReadAllBytes(outbound);
        callers["M2M__AllowedCallerClientIds__2"] = "netratel-mcp-http";
        Source(api: callers, mcp: new()
        {
            ["NetRatel__Mcp__Outbound__ApiM2MClientId"] = "netratel-mcp-http", ["NetRatel__Mcp__Outbound__ApiM2MClientSecretFile"] = outbound,
            ["NetRatel__Mcp__Delegation__ServicePrincipal"] = "netratel-mcp-http", ["NetRatel__Mcp__Delegation__SharedKeyBase64File"] = delegation
        });
        Assert.NotNull(NetRatelHttpHostingSetupService.LoadRequest(_directory));
        Assert.Equal(before, File.ReadAllBytes(outbound));
    }

    [Fact]
    public void Environment_secret_file_and_compact_identity_replace_lower_protected_credential_sources()
    {
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var secretPath = Path.Combine(_directory, "existing.secret");
        ConfigurationFileStorage.WriteAtomic(secretPath, "winning-protected-secret\n");
        var source = Source(mcp: new()
        {
            ["NETRATEL_MCP_OUTBOUND_API_M2M_CLIENT_ID"] = "netratel-mcp-http",
            ["NetRatel__Mcp__Outbound__ApiM2MClientSecretFile"] = secretPath
        }, protectedConfiguration: new()
        {
            ["NetRatel:Mcp:Http:Instance"] = "prod", ["NetRatel:Mcp:Http:ProdApiBaseUrl"] = Request.ApiBaseUrl,
            ["NetRatel:Mcp:Outbound:ApiBaseUrl"] = Request.ApiBaseUrl, ["NetRatel:Mcp:Outbound:ApiM2MTokenUrl"] = Request.ApiBaseUrl + "/connect/token",
            ["NetRatel:Mcp:Outbound:ApiM2MClientId"] = "lower-protected-identity", ["NetRatel:Mcp:Outbound:ApiM2MClientSecret"] = "masked-lower-secret",
            ["NetRatel:Mcp:Outbound:ApiM2MScope"] = "netratel.api"
        });
        var existing = ExistingHttpHostSettings.Discover(name => name == HttpHostingEffectiveDeployment.SourceEnvironmentVariable ? source.SourcePath : null);
        var exported = existing.MaterializeOutboundConfiguration(Path.Combine(_directory, "adopted"));
        var configuration = NetRatelConfigurationService.ReadConfiguration(exported!);
        Assert.Equal("netratel-mcp-http", configuration.ApiM2MClientId);
        Assert.Equal(secretPath, configuration.ApiM2MClientSecretFile);
        Assert.True(string.IsNullOrEmpty(configuration.ApiM2MClientSecret));
        Assert.Equal("winning-protected-secret", NetRatelConfigurationService.ResolveSecretReferences(configuration).ApiM2MClientSecret);
        Assert.Equal("winning-protected-secret\n", File.ReadAllText(secretPath));
    }

    [Theory]
    [InlineData("RequiredGroups")]
    [InlineData("RequiredScopes")]
    [InlineData("AllowedOrigins")]
    public void Discovery_replaces_http_arrays_from_the_highest_provider_instead_of_merging_lower_indices(string name)
    {
        var higher = name == "AllowedOrigins" ? "https://higher.example.invalid" : "higher.value";
        var source = Source(mcp: new() { ["NetRatel__Mcp__Http__" + name + "__0"] = higher }, protectedConfiguration: new()
        {
            ["NetRatel:Mcp:Http:" + name + ":0"] = name == "AllowedOrigins" ? "https://lower0.example.invalid" : "lower.value0",
            ["NetRatel:Mcp:Http:" + name + ":1"] = name == "AllowedOrigins" ? "https://lower1.example.invalid" : "lower.value1"
        });
        var existing = ExistingHttpHostSettings.Discover(key => key == HttpHostingEffectiveDeployment.SourceEnvironmentVariable ? source.SourcePath : null);
        var selected = name == "RequiredGroups" ? existing.RequiredGroups : name == "RequiredScopes" ? existing.RequiredScopes : existing.AllowedOrigins;
        Assert.Equal([higher], selected);
    }

    [Fact]
    public void Saved_metadata_without_explicit_manager_cannot_infer_release_ownership()
    {
        new NetRatelHttpHostingSetupService().Apply(Request);
        var secret = Path.Combine(_directory, "secrets", "outbound-m2m.secret");
        var before = File.ReadAllBytes(secret);
        var path = Path.Combine(_directory, ".http-host.setup.json");
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(saved.Remove(nameof(HttpHostingSetupRequest.DeploymentManager)));
        ConfigurationFileStorage.WriteAtomic(path, saved.ToJsonString(), replace: true);
        var exception = Assert.Throws<AgentClientValidationException>(() => NetRatelHttpHostingSetupService.LoadRequest(_directory));
        Assert.StartsWith("deployment_manager_unknown:", exception.Message);
        Assert.Equal(before, File.ReadAllBytes(secret));
    }

    [Fact]
    public void Runtime_verification_uses_actual_private_providers_and_image_identity()
    {
        var source = Source(protectedConfiguration: new() { ["NetRatel:Mcp:Http:ReadScope"] = "custom.read" }, imageId: "sha256:reviewed-image");
        var mcp = Mcp();
        var api = Api();
        source.Prepare(Request, [], api, mcp);
        ConfigurationFileStorage.WriteAtomic(Path.Combine(_directory, ".effective-proposal.json"), source.RedactedProposal(Request, new Dictionary<string, string>(), api, mcp));
        var runtimePath = Path.Combine(_directory, ".runtime.json");
        void SaveRuntime(string scope, string image, bool invalidMaximum = false)
        {
            var configuration = new Dictionary<string, string?> { ["NetRatel:Mcp:Http:ReadScope"] = scope };
            if (invalidMaximum) configuration["NetRatel:Mcp:Http:MaxRequestBodyBytes"] = "0";
            var runtime = new Dictionary<string, object>
            {
                ["web"] = new Dictionary<string, string>(), ["api"] = api, ["mcp-http"] = mcp,
                ["x-netratel-protected-configuration"] = new Dictionary<string, object> { ["web"] = new Dictionary<string, string>(), ["api"] = new Dictionary<string, string>(), ["mcp-http"] = configuration },
                ["x-netratel-runtime-images"] = new Dictionary<string, string> { ["mcp-http"] = image }
            };
            ConfigurationFileStorage.WriteAtomic(runtimePath, JsonSerializer.Serialize(runtime), replace: File.Exists(runtimePath));
        }
        SaveRuntime("custom.read", "sha256:reviewed-image");
        HttpHostingEffectiveDeployment.ValidateRuntimeProposal(_directory, (_, _) => null, runtimePath);
        SaveRuntime("stale.read", "sha256:reviewed-image");
        Assert.StartsWith("effective_runtime_mismatch:", Assert.Throws<AgentClientValidationException>(() => HttpHostingEffectiveDeployment.ValidateRuntimeProposal(_directory, (_, _) => null, runtimePath)).Message);
        SaveRuntime("custom.read", "sha256:stale-image");
        Assert.StartsWith("effective_runtime_mismatch:", Assert.Throws<AgentClientValidationException>(() => HttpHostingEffectiveDeployment.ValidateRuntimeProposal(_directory, (_, _) => null, runtimePath)).Message);
        SaveRuntime("custom.read", "sha256:reviewed-image", invalidMaximum: true);
        Assert.StartsWith("serving_options_invalid:", Assert.Throws<AgentClientValidationException>(() => HttpHostingEffectiveDeployment.ValidateRuntimeProposal(_directory, (_, _) => null, runtimePath)).Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

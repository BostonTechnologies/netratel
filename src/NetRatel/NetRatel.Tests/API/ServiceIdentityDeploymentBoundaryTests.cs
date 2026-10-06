using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Security.M2M;
using NetRatel.Infrastructure.ServiceIdentity;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ServiceIdentityDeploymentBoundaryTests
{
    [Fact]
    public void Historical_underscore_alias_resolves_one_complete_canonical_profile()
    {
        var resolver = Resolver(new() { ["M2MClients:rateldesk_orchestrator:Secret"] = new string('s', 40),
            ["M2MClients:rateldesk_orchestrator:AllowedAudiences:0"] = "netratel.api", ["M2MClients:rateldesk_orchestrator:AllowedScopes:0"] = "netratel.api" });
        var profile = Assert.IsType<M2MDeploymentProfile>(resolver.Resolve("rateldesk-orchestrator"));
        Assert.Equal("netratel.api", Assert.Single(profile.AllowedAudiences));
        Assert.Null(resolver.Resolve("rateldesk_orchestrator"));
        Assert.True(resolver.OwnsIdentity("rateldesk_orchestrator"));
    }
    [Fact]
    public void Exact_and_alias_shadows_fail_closed_even_when_secrets_match()
    {
        var resolver = Resolver(new() { ["M2MClients:rateldesk_orchestrator:Secret"] = new string('s', 40),
            ["M2MClients:rateldesk-orchestrator:Secret"] = new string('s', 40) });
        Assert.Throws<ServiceClientConflictException>(() => resolver.List());
    }
    [Fact]
    public void Incomplete_configured_alias_never_falls_back_to_a_managed_database_identity()
    {
        var empty = Resolver(new() { ["M2MClients:synthetic_alias:Secret"] = "" });
        Assert.True(empty.OwnsIdentity("synthetic-alias"));
        var incomplete = Resolver(new() { ["M2MClients:synthetic_alias:AllowedScopes:0"] = "netratel.api" });
        Assert.True(incomplete.OwnsIdentity("synthetic-alias"));
        Assert.Null(incomplete.Resolve("synthetic-alias"));
    }
    [Fact]
    public void Packaged_canonical_metadata_and_underscore_secret_resolve_one_legacy_profile()
    {
        var values = new Dictionary<string, string?>
        {
            ["M2M:Authority"] = "https://api.example.test", ["M2M:Audience"] = "netratel.api",
            ["M2M:AllowedCallerClientIds:0"] = "netratel.cli",
            ["M2MClients:netratel.cli:AllowedAudiences:0"] = "netratel.api",
            ["M2MClients:netratel.cli:AllowedScopes:0"] = "netratel.api",
            ["M2MClients:netratel_cli:Secret"] = new string('s', 40)
        };
        var resolver = new M2MDeploymentProfileResolver(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var profile = Assert.IsType<M2MDeploymentProfile>(resolver.Resolve("netratel.cli"));
        Assert.Equal("netratel.api", Assert.Single(profile.AllowedAudiences));
        Assert.Equal("netratel.api", Assert.Single(profile.AllowedScopes));
        Assert.Single(resolver.List());
        Assert.True(resolver.OwnsIdentity("netratel_cli"));
    }
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Empty_optional_alias_with_packaged_metadata_is_reserved_nonissuable_and_does_not_block_other_clients(string secret)
    {
        var values = PackagedMetadata();
        values["M2MClients:netratel_cli:Secret"] = secret;
        var resolver = new M2MDeploymentProfileResolver(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        Assert.Empty(resolver.List());
        Assert.Null(resolver.Resolve("netratel.cli"));
        Assert.Null(resolver.Resolve("netratel_cli"));
        Assert.True(resolver.OwnsIdentity("netratel.cli"));
        Assert.True(resolver.OwnsIdentity("netratel_cli"));
        Assert.False(resolver.OwnsIdentity("unrelated-managed-client"));
    }
    [Fact]
    public void Metadata_only_aliases_with_conflicting_allowed_sets_still_fail_closed()
    {
        var values = PackagedMetadata();
        values["M2MClients:netratel_cli:Secret"] = "";
        values["M2MClients:netratel_cli:AllowedScopes:0"] = "unapproved.scope";
        var resolver = new M2MDeploymentProfileResolver(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.Throws<ServiceClientConflictException>(() => resolver.List());
        Assert.Throws<ServiceClientConflictException>(() => resolver.OwnsIdentity("unrelated-managed-client"));
    }
    [Fact]
    public void Monitored_empty_alias_can_be_completed_on_reload_with_one_canonical_profile()
    {
        var values = PackagedMetadata();
        values["M2MClients:netratel_cli:Secret"] = "";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddOptions<M2MOptions>().Bind(configuration.GetSection("M2M"));
        services.AddNetRatelServiceIdentityApi(configuration);
        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IM2MDeploymentProfileResolver>();
        Assert.Empty(resolver.List());
        Assert.True(resolver.OwnsIdentity("netratel_cli"));
        Assert.False(resolver.OwnsIdentity("unrelated-managed-client"));

        configuration["M2MClients:netratel_cli:Secret"] = new string('s', 40);
        configuration.Reload();
        var profile = Assert.Single(resolver.List());
        Assert.Equal("netratel.cli", profile.ClientId);
        Assert.Equal("netratel.api", Assert.Single(profile.AllowedAudiences));
        Assert.Equal("netratel.api", Assert.Single(profile.AllowedScopes));
        Assert.NotNull(resolver.Resolve("netratel.cli"));
        Assert.Null(resolver.Resolve("netratel_cli"));

        configuration["M2MClients:netratel_cli:Secret"] = "";
        configuration.Reload();
        Assert.Empty(resolver.List());
        Assert.True(resolver.OwnsIdentity("netratel.cli"));
        Assert.False(resolver.OwnsIdentity("unrelated-managed-client"));
    }
    [Fact]
    public async Task Token_cache_separates_profiles_and_checks_live_authority_before_cached_return()
    {
        var handler = new TokenHandler(); var service = new ClientCredentialsTokenService(new Factory(new HttpClient(handler)));
        var enabled = true;
        DownstreamApiOptions Target(string id, long revision) => new() { BaseUrl = "https://peer.example.test", Audience = "peer.services",
            Scope = "narrow.read", TokenEndpoint = "https://peer.example.test/connect/token", ClientId = id, ClientSecret = new string('s', 40),
            ProfileIdentity = "profile", ProfileRevision = revision, ClientSecretPost = true, CurrentAuthority = _ => Task.FromResult(enabled) };
        Assert.Equal("token-1", await service.GetTokenAsync(Target("client-a", 1)));
        Assert.Equal("token-1", await service.GetTokenAsync(Target("client-a", 1)));
        Assert.Equal("token-2", await service.GetTokenAsync(Target("client-b", 1)));
        Assert.Equal("token-3", await service.GetTokenAsync(Target("client-a", 2)));
        enabled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetTokenAsync(Target("client-a", 1)));
        Assert.Equal(3, handler.Bodies.Count);
        Assert.All(handler.Bodies, x => Assert.Contains("client_secret=", x));
    }
    private static M2MDeploymentProfileResolver Resolver(Dictionary<string, string?> additions)
    {
        additions["M2M:Authority"] = "https://api.example.test"; additions["M2M:Audience"] = "netratel.api";
        additions["M2M:AllowedCallerClientIds:0"] = "rateldesk-orchestrator";
        return new(new ConfigurationBuilder().AddInMemoryCollection(additions).Build());
    }
    private static Dictionary<string, string?> PackagedMetadata() => new()
    {
        ["M2M:Authority"] = "https://api.example.test", ["M2M:Audience"] = "netratel.api",
        ["M2M:AllowedCallerClientIds:0"] = "netratel.cli",
        ["M2MClients:netratel.cli:AllowedAudiences:0"] = "netratel.api",
        ["M2MClients:netratel.cli:AllowedScopes:0"] = "netratel.api"
    };
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class TokenHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Null(request.Headers.Authorization); Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent($$"""{"access_token":"token-{{Bodies.Count}}","token_type":"Bearer","expires_in":300}""", System.Text.Encoding.UTF8, "application/json") };
        }
    }
}

using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Security.Integration;
using NetRatel.API.Security.Local;
using NetRatel.Infrastructure.Identity.Authorization;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class MonitoringFlowPermissionAuthorizationTests
{
    private static readonly string[] AccountPolicies =
    [
        MonitoringAuthorization.ReadPolicy, MonitoringAuthorization.ReadOrManagePolicy,
        MonitoringAuthorization.PermissionsSummaryPolicy, MonitoringAuthorization.DiscoveryPolicy,
        ClientMetadataAuthorization.ReadPolicy, ClientMetadataAuthorization.WritePolicy,
        NetclawAuthorization.ReadPolicy, NetclawAuthorization.ManagePolicy, NetclawAuthorization.SecretUsePolicy,
        NetclawAuthorization.ConnectionAdminPolicy, NetclawAuthorization.DiscoveryPolicy
    ];

    public static IEnumerable<object[]> UnsupportedPrincipals()
    {
        foreach (var scenario in new[] { "M2M", "ManagedService", "System", "Agent", "MachineToken", "Test", "service_claim",
            "service_token_use", "http_mcp", "incomplete_credential", "mixed_local", "mixed_credential", "mixed_managed", "extra_unauthenticated" })
            yield return [scenario];
    }

    private static ClaimsPrincipal Unsupported(string scenario) => scenario switch
    {
        "service_claim" => new(Account("Oidc", new Claim("auth_mode", "service"))),
        "service_token_use" => new(Account("Oidc", new Claim("token_use", "netratel_service"))),
        "http_mcp" => new(Integration("http_mcp")),
        "incomplete_credential" => new(new ClaimsIdentity([new Claim("auth_mode", "integration_credential")], IntegrationCredentialAuthenticationHandler.SchemeName)),
        "mixed_local" => new([Account("Oidc"), Account(LocalAuthenticationOptions.Scheme)]),
        "mixed_credential" => new([Account("Oidc"), Integration("api")]),
        "mixed_managed" => new([Account("Oidc"), Account("ManagedService")]),
        "extra_unauthenticated" => new([Account("Oidc"), new ClaimsIdentity([new Claim("role", "Operator")])]),
        _ => new(new ClaimsIdentity([new Claim("role", "Operator")], scenario))
    };

    [Theory]
    [MemberData(nameof(UnsupportedPrincipals))]
    public async Task Named_account_permission_policies_reject_unsupported_or_mixed_principals_even_if_evaluator_returns_admin(string scenario)
    {
        var principal = Unsupported(scenario);
        await using var provider = Services(new PermissiveCurrentAccess()).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var http = Resource(principal);
        foreach (var policy in AccountPolicies)
            (await authorization.AuthorizeAsync(principal, http, policy)).Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData("Oidc")]
    [InlineData(LocalAuthenticationOptions.Scheme)]
    [InlineData(IntegrationCredentialAuthenticationHandler.SchemeName)]
    public async Task A_single_supported_account_identity_keeps_the_existing_current_permission_evaluator(string scheme)
    {
        var principal = new ClaimsPrincipal(scheme == IntegrationCredentialAuthenticationHandler.SchemeName ? Integration("api") : Account(scheme));
        await using var provider = Services(new PermissiveCurrentAccess()).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var http = Resource(principal);
        var policies = scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>();
        foreach (var policy in AccountPolicies)
        {
            (await authorization.AuthorizeAsync(principal, http, policy)).Succeeded.Should().BeTrue();
            (await policies.GetPolicyAsync(policy))!.AuthenticationSchemes.Should().Equal("Bearer");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Captured_discovery_scope_intersects_fresh_scope_and_never_turns_empty_into_unrestricted(bool unrestricted)
    {
        var principal = new ClaimsPrincipal(Account("Oidc"));
        var access = new PermissiveCurrentAccess { Scope = unrestricted ? null : [7] };
        await using var provider = Services(access).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var http = Resource(principal);
        (await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, http, MonitoringAuthorization.DiscoveryPolicy)).Succeeded.Should().BeTrue();
        MonitoringFlowPermissionAuthorization.RestrictDiscoveryTenantIds(http, NetRatelPermissions.MonitoringRead, [])
            .Should().NotBeNull().And.BeEmpty();
        MonitoringFlowPermissionAuthorization.RestrictDiscoveryTenantIds(http, NetRatelPermissions.MonitoringRead, [8])
            .Should().Equal(unrestricted ? new[] { 8 } : Array.Empty<int>());
        var freshUnrestricted = MonitoringFlowPermissionAuthorization.RestrictDiscoveryTenantIds(http, NetRatelPermissions.MonitoringRead, null);
        if (unrestricted) freshUnrestricted.Should().BeNull();
        else freshUnrestricted.Should().Equal(7);
    }

    [Fact]
    public async Task Empty_discovery_admission_succeeds_for_safe_empty_response_but_never_reveals_fresh_rows()
    {
        var principal = new ClaimsPrincipal(Integration("api"));
        await using var provider = Services(new PermissiveCurrentAccess { Scope = [] }).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var http = Resource(principal);
        (await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, http, MonitoringAuthorization.DiscoveryPolicy)).Succeeded.Should().BeTrue();
        MonitoringFlowPermissionAuthorization.RestrictDiscoveryTenantIds(http, NetRatelPermissions.MonitoringRead, null)
            .Should().NotBeNull().And.BeEmpty();
        MonitoringFlowPermissionAuthorization.RestrictDiscoveryRows(http, NetRatelPermissions.MonitoringRead, new[] { 7, 8 }, id => id)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Ai_discovery_captures_telemetry_scope_independently_of_monitoring_scope()
    {
        var principal = new ClaimsPrincipal(Integration("api"));
        await using var provider = Services(new PermissiveCurrentAccess { Scope = [7] }).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var http = Resource(principal);
        (await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, http, NetclawAuthorization.DiscoveryPolicy)).Succeeded.Should().BeTrue();

        MonitoringFlowPermissionAuthorization.RestrictDiscoveryTenantIds(http, NetRatelPermissions.TelemetryRead, null)
            .Should().Equal(7);
        MonitoringFlowPermissionAuthorization.RestrictDiscoveryRows(http, NetRatelPermissions.TelemetryRead, new[] { 7, 8 }, id => id)
            .Should().Equal(7);
        var unrelated = () => MonitoringFlowPermissionAuthorization.RestrictDiscoveryTenantIds(http, NetRatelPermissions.MonitoringRead, null);
        unrelated.Should().Throw<UnauthorizedAccessException>();
    }

    private static ClaimsIdentity Account(string scheme, params Claim[] additional) =>
        new([new Claim("netratel_principal_id", "isolated-current-principal"), .. additional], scheme);
    private static ClaimsIdentity Integration(string purpose) => Account(IntegrationCredentialAuthenticationHandler.SchemeName,
        new Claim("auth_mode", "integration_credential"), new Claim("integration_credential_purpose", purpose),
        new Claim(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType, "isolated-current-credential"));
    private static HttpContext Resource(ClaimsPrincipal principal)
    {
        var http = new DefaultHttpContext { User = principal };
        http.Request.RouteValues["tenantId"] = 7;
        return http;
    }
    private static IServiceCollection Services(IEffectiveAccessService access)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            MonitoringAuthorization.AddPolicies(options);
            ClientMetadataAuthorization.AddPolicies(options);
            NetclawAuthorization.AddPolicies(options);
        });
        services.AddSingleton(access);
        services.AddScoped<IAuthorizationHandler, EffectiveAccessHandler>();
        MonitoringFlowPermissionAuthorization.AddHandlers(services);
        return services;
    }
    private sealed class PermissiveCurrentAccess : IEffectiveAccessService
    {
        public int[]? Scope { get; init; } = [7];
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSnapshot("isolated-current-principal", false, true, NetRatelPermissions.All.ToHashSet()));
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal principal, string permission, CancellationToken cancellationToken = default) => Task.FromResult(Scope);
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

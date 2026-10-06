using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.API.Security.Integration;
using NetRatel.API.Security.Local;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Security.Authorization;

/// <summary>Permission gates for the existing Monitoring and Flow account APIs.</summary>
public static class MonitoringFlowPermissionAuthorization
{
    private static readonly object DiscoveryScopesKey = new();

    public static void AddSinglePolicy(AuthorizationOptions options, string policyName, string permission)
    {
        RequireKnownPermission(permission);
        options.AddPolicy(policyName, policy => AccountPolicy(policy)
            .AddRequirements(new EffectiveAccessRequirement(permission)));
    }

    public static void AddAnyPolicy(AuthorizationOptions options, string policyName, params string[] permissions)
    {
        if (permissions.Length == 0)
            throw new ArgumentException("At least one existing permission is required.", nameof(permissions));
        foreach (var permission in permissions)
            RequireKnownPermission(permission);
        options.AddPolicy(policyName, policy => AccountPolicy(policy)
            .AddRequirements(new MonitoringFlowAnyPermissionRequirement(permissions)));
    }

    public static void AddDiscoveryPolicy(AuthorizationOptions options, string policyName, string permission)
    {
        RequireKnownPermission(permission);
        options.AddPolicy(policyName, policy => AccountPolicy(policy)
            .AddRequirements(new MonitoringFlowTenantDiscoveryRequirement(permission)));
    }

    public static void AddHandlers(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, MonitoringFlowAnyPermissionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, MonitoringFlowTenantDiscoveryHandler>());
    }

    public static int[]? RestrictDiscoveryTenantIds(HttpContext http, string permission, int[]? currentScope)
    {
        var admittedScope = AdmittedDiscoveryScope(http, permission);
        if (admittedScope is null)
            return currentScope;
        if (currentScope is null)
            return admittedScope.ToArray();
        return currentScope.Intersect(admittedScope).Order().ToArray();
    }

    public static T[] RestrictDiscoveryRows<T>(HttpContext http, string permission, IEnumerable<T> currentRows, Func<T, int> tenantId)
    {
        var admittedScope = AdmittedDiscoveryScope(http, permission);
        return admittedScope is null
            ? currentRows.ToArray()
            : currentRows.Where(row => admittedScope.Contains(tenantId(row))).ToArray();
    }

    internal static bool IsSupportedAccount(ClaimsPrincipal principal)
    {
        if (principal.HasClaim("auth_mode", "service") || principal.HasClaim("token_use", "netratel_service"))
            return false;
        var identities = principal.Identities.ToArray();
        // The production selector and its three account handlers produce one
        // identity. Reject mixed principals before the current evaluator reads
        // principal-wide claims, including a second unauthenticated identity.
        return identities.Length == 1 && identities[0].IsAuthenticated && identities.All(identity =>
            string.Equals(identity.AuthenticationType, "Oidc", StringComparison.Ordinal) ||
            string.Equals(identity.AuthenticationType, LocalAuthenticationOptions.Scheme, StringComparison.Ordinal) ||
            (string.Equals(identity.AuthenticationType, IntegrationCredentialAuthenticationHandler.SchemeName, StringComparison.Ordinal) &&
             identity.HasClaim("auth_mode", "integration_credential") &&
             identity.HasClaim("integration_credential_purpose", "api") &&
             !string.IsNullOrWhiteSpace(identity.FindFirst("netratel_principal_id")?.Value) &&
             !string.IsNullOrWhiteSpace(identity.FindFirst(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType)?.Value)));
    }

    internal static void CaptureDiscoveryScope(HttpContext http, string permission, int[]? scope)
    {
        if (http.Items[DiscoveryScopesKey] is not Dictionary<string, int[]?> scopes)
        {
            scopes = new(StringComparer.Ordinal);
            http.Items[DiscoveryScopesKey] = scopes;
        }
        var captured = scope?.Distinct().Order().ToArray();
        if (scopes.TryGetValue(permission, out var earlier) && earlier is not null)
            captured = captured is null ? earlier : captured.Intersect(earlier).Order().ToArray();
        scopes[permission] = captured;
    }

    private static int[]? AdmittedDiscoveryScope(HttpContext http, string permission)
    {
        if (!IsSupportedAccount(http.User) ||
            http.Items[DiscoveryScopesKey] is not Dictionary<string, int[]?> scopes ||
            !scopes.TryGetValue(permission, out var scope))
            throw new UnauthorizedAccessException("Permission-bound tenant discovery was not admitted.");
        return scope;
    }

    private static AuthorizationPolicyBuilder AccountPolicy(AuthorizationPolicyBuilder policy) => policy
        // One selector prevents combining a cookie principal with a different
        // bearer credential. The selector already authenticates each supported
        // account alternative using its existing production handler.
        .AddAuthenticationSchemes("Bearer")
        .RequireAuthenticatedUser()
        .RequireAssertion(context => IsSupportedAccount(context.User));

    private static void RequireKnownPermission(string permission)
    {
        if (!NetRatelPermissions.All.Contains(permission))
            throw new ArgumentException("Only a catalogued product permission can be registered.", nameof(permission));
    }
}

public sealed class MonitoringFlowAnyPermissionRequirement(IEnumerable<string> permissions) : IAuthorizationRequirement
{
    public IReadOnlyList<string> Permissions { get; } = permissions.Distinct(StringComparer.Ordinal).ToArray();
}

public sealed class MonitoringFlowAnyPermissionHandler(IEffectiveAccessService access)
    : AuthorizationHandler<MonitoringFlowAnyPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, MonitoringFlowAnyPermissionRequirement requirement)
    {
        if (!MonitoringFlowPermissionAuthorization.IsSupportedAccount(context.User) || context.Resource is not HttpContext http ||
            !int.TryParse(http.Request.RouteValues["tenantId"]?.ToString(), out var tenantId) || tenantId <= 0)
            return;
        foreach (var permission in requirement.Permissions)
        {
            if (NetRatelPermissions.All.Contains(permission) &&
                await access.AuthorizeAsync(context.User, permission, tenantId, http.RequestAborted).ConfigureAwait(false))
            {
                context.Succeed(requirement);
                return;
            }
        }
    }
}

public sealed class MonitoringFlowTenantDiscoveryRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class MonitoringFlowTenantDiscoveryHandler(IEffectiveAccessService access)
    : AuthorizationHandler<MonitoringFlowTenantDiscoveryRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, MonitoringFlowTenantDiscoveryRequirement requirement)
    {
        if (!MonitoringFlowPermissionAuthorization.IsSupportedAccount(context.User) || context.Resource is not HttpContext http ||
            !NetRatelPermissions.All.Contains(requirement.Permission))
            return;
        var scope = await access.GetAuthorizedTenantIdsAsync(context.User, requirement.Permission, http.RequestAborted).ConfigureAwait(false);
        if (scope is not null && scope.Any(tenantId => tenantId <= 0))
            return;
        MonitoringFlowPermissionAuthorization.CaptureDiscoveryScope(http, requirement.Permission, scope);
        // An empty permission-filtered result is the established safe 200[]
        // discovery behavior, and never becomes unrestricted instance access.
        context.Succeed(requirement);
    }
}

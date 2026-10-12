using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.API.Security.Integration;
using NetRatel.API.Security.M2M;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Security.Authorization;

/// <summary>Admits account callers or an authenticated MCP relay carrying verified caller attribution.</summary>
public static class ManagedOperationAuthorization
{
    public const string PolicyName = "ManagedOperationAccess";
    public const string AuthenticationScheme = "ManagedOperationCaller";

    public static AuthenticationBuilder AddAuthentication(AuthenticationBuilder builder) =>
        builder.AddPolicyScheme(AuthenticationScheme, AuthenticationScheme, options =>
        {
            // This header only selects an authenticator. It supplies no authority
            // until both the relay token and the signed delegation are verified.
            options.ForwardDefaultSelector = http =>
                http.Request.Headers.ContainsKey(McpOperatorDelegationOptions.HeaderName) ? "M2M" : "Bearer";
        });

    public static void AddPolicies(AuthorizationOptions options) =>
        options.AddPolicy(PolicyName, policy => policy
            .AddAuthenticationSchemes(AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new ManagedOperationCallerRequirement()));

    public static void AddHandlers(IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, ManagedOperationCallerHandler>());

    internal static async Task<bool> IsAllowedRelayAsync(HttpContext http, ClaimsPrincipal caller, M2MOptions options)
    {
        // The JWT identity's AuthenticationType is not proof of which validator
        // accepted it. Require the actual M2M handler result and its existing
        // configured-caller requirement, without merging account identities.
        var relay = await http.AuthenticateAsync("M2M").ConfigureAwait(false);
        if (!relay.Succeeded || relay.Principal is null || relay.Principal.Identities.Count() != 1 ||
            relay.Principal.Identity?.IsAuthenticated != true || caller.Identities.Count() != 1)
            return false;
        var allowedClient = new AllowedClientRequirement(options.AllowedCallerClientIds);
        var relayContext = new AuthorizationHandlerContext([allowedClient], relay.Principal, http);
        await new AllowedClientHandler().HandleAsync(relayContext).ConfigureAwait(false);
        return relayContext.HasSucceeded;
    }
}

public sealed class ManagedOperationCallerRequirement : IAuthorizationRequirement;

public sealed class ManagedOperationCallerHandler(IOptions<M2MOptions> relayOptions, IHostEnvironment environment)
    : AuthorizationHandler<ManagedOperationCallerRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ManagedOperationCallerRequirement requirement)
    {
        if (context.Resource is not HttpContext http)
            return;
        if (!http.Request.Headers.ContainsKey(McpOperatorDelegationOptions.HeaderName))
        {
            if (!http.TryGetMcpOperatorDelegation(out _) && MonitoringFlowPermissionAuthorization.IsSupportedAccount(context.User))
                context.Succeed(requirement);
            return;
        }

        var descriptor = ManagedOperationCatalog.Find(http.Request.RouteValues["tool"]?.ToString() ?? string.Empty,
            http.Request.RouteValues["operation"]?.ToString() ?? string.Empty);
        if (descriptor is null || !http.TryGetMcpOperatorDelegation(out var delegation) || delegation is null ||
            delegation.Tool != descriptor.Tool || delegation.Operation != descriptor.Operation ||
            !McpOperatorRuntimeEnvironment.TryResolve(environment, out _, out var instance) || delegation.Instance != instance ||
            string.IsNullOrWhiteSpace(delegation.Resource) ||
            !McpLocalDelegationTargetRequirements.TryGetAuthorizationTenant(delegation, out var tenantId) || tenantId is not > 0 ||
            descriptor.AgentTarget != delegation.AgentId.HasValue || delegation.AgentId == Guid.Empty ||
            (delegation.IngressCredentialId is null && string.IsNullOrWhiteSpace(delegation.Identity.ExternalIssuer)))
            return;

        if (await ManagedOperationAuthorization.IsAllowedRelayAsync(http, context.User, relayOptions.Value).ConfigureAwait(false))
            context.Succeed(requirement);
        // ManagedOperationAdmission subsequently binds the signed tenant/agent
        // to the closed request and rechecks its account, grants and MCP policy.
    }
}

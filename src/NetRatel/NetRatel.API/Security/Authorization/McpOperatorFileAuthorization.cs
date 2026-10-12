using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.API.Security.M2M;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Security.Authorization;

/// <summary>Exact catalog operation attached by each server-owned file route.</summary>
public sealed record McpOperatorFileOperationMetadata(string Operation);

public static class McpOperatorFileAuthorization
{
    public const string PolicyName = "McpOperatorFileAccess";

    public static void AddPolicies(AuthorizationOptions options) =>
        options.AddPolicy(PolicyName, policy => policy
            .AddAuthenticationSchemes(ManagedOperationAuthorization.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new McpOperatorFileCallerRequirement()));

    public static void AddHandlers(IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, McpOperatorFileCallerHandler>());
}

public sealed class McpOperatorFileCallerRequirement : IAuthorizationRequirement;

public sealed class McpOperatorFileCallerHandler(IOptions<M2MOptions> relayOptions, IHostEnvironment environment)
    : AuthorizationHandler<McpOperatorFileCallerRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, McpOperatorFileCallerRequirement requirement)
    {
        if (context.Resource is not HttpContext http ||
            http.GetEndpoint()?.Metadata.GetMetadata<McpOperatorFileOperationMetadata>() is not { } metadata ||
            McpOperationAccessCatalog.Find("netratel_files", metadata.Operation) is null ||
            !int.TryParse(http.Request.RouteValues["tenantId"]?.ToString(), out var tenantId) || tenantId <= 0 ||
            !Guid.TryParse(http.Request.RouteValues["agentId"]?.ToString(), out var agentId) || agentId == Guid.Empty)
            return;

        if (!http.Request.Headers.ContainsKey(McpOperatorDelegationOptions.HeaderName))
        {
            if (!http.TryGetMcpOperatorDelegation(out _) && MonitoringFlowPermissionAuthorization.IsSupportedAccount(context.User))
                context.Succeed(requirement);
            return;
        }

        if (!http.TryGetMcpOperatorDelegation(out var delegation) || delegation is null ||
            delegation.Tool != "netratel_files" || delegation.Operation != metadata.Operation ||
            delegation.TenantId != tenantId || delegation.AgentId != agentId || delegation.ObjectReference is not null ||
            !McpOperatorRuntimeEnvironment.TryResolve(environment, out _, out var instance) || delegation.Instance != instance ||
            string.IsNullOrWhiteSpace(delegation.Resource) || string.IsNullOrWhiteSpace(delegation.CorrelationId))
            return;
        if (await ManagedOperationAuthorization.IsAllowedRelayAsync(http, context.User, relayOptions.Value).ConfigureAwait(false))
            context.Succeed(requirement);
        // The file admission path rechecks canonical account grants, current
        // target policy and exact roots before binding or consuming any plan.
    }
}

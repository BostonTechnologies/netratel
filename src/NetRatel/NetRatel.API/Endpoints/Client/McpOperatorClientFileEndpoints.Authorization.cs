using System.Security.Claims;
using NetRatel.API.Middleware;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Security.Integration;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

public static partial class McpOperatorClientFileEndpoints
{
    private static async Task<(McpOperatorFileActor? Actor, string? FailureCode)> ResolveFileActorAsync(
        HttpContext http, string instance, int tenantId, Guid agentId, string operation,
        string delegatedOperation, McpOperationAccessScope scope, CancellationToken cancellationToken)
    {
        if (http.TryGetMcpOperatorDelegation(out var delegation) && delegation is not null)
        {
            if (delegation.Instance != instance || delegation.Tool != Tool || delegation.Operation != delegatedOperation ||
                delegation.TenantId != tenantId || delegation.AgentId != agentId ||
                string.IsNullOrWhiteSpace(delegation.Resource) || string.IsNullOrWhiteSpace(delegation.CorrelationId))
                return (null, "delegated_identity_invalid");

            // The dedicated ingress policy proves M2M authentication; the
            // delegation middleware rechecks the signed account's file grant.
            return (new(delegation.Identity, delegation.ServicePrincipal, delegation.Resource,
                delegation.CorrelationId, delegation.RequestId, null), null);
        }

        if (http.Request.Headers.ContainsKey(McpOperatorDelegationOptions.HeaderName) ||
            !MonitoringFlowPermissionAuthorization.IsSupportedAccount(http.User) ||
            http.User.FindFirstValue("netratel_principal_id") is not { Length: > 0 } owner)
            return (null, "delegated_identity_required");

        var user = http.User.Clone();
        var access = http.RequestServices.GetRequiredService<IEffectiveAccessService>();
        if (!await McpLocalDelegationPermissionMapper.AuthorizeAsync(access, user, Tool, operation, tenantId, cancellationToken).ConfigureAwait(false))
            return (null, "permission_denied");

        McpOperatorDelegationIdentity? identity;
        if (user.Identity!.AuthenticationType == "Oidc")
        {
            if (!McpOperatorDelegationIdentityFactory.TryCreate(user, out identity))
                return (null, "delegated_identity_invalid");
        }
        else
        {
            // Derive only the checked permission's MCP scope. The credential ID
            // remains distinct from its owner, including in opaque plan hashes.
            identity = new(owner, user.FindFirstValue(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType),
                "netratel-direct-account", [], McpLocalDelegationPermissionMapper.RolesFor(scope),
                [McpOperationAccessScopeNames.Canonical(scope)]);
        }

        return (new(identity!, "netratel-direct-account", "netratel-api", http.TraceIdentifier,
            Guid.NewGuid().ToString("N"), new(user, access)), null);
    }

    private static Task<bool> HasCurrentFilePermissionAsync(McpOperatorFileRouteContext context, CancellationToken cancellationToken) =>
        context.DirectCaller is { } caller
            ? McpLocalDelegationPermissionMapper.AuthorizeAsync(caller.Access, caller.User, Tool,
                context.Operation, context.TenantId, cancellationToken)
            : Task.FromResult(true);

    private static async Task<McpOperatorAcceptedAudit> RecordFileAcceptedAuditAsync(
        IMcpOperatorRouteAdmission admission, McpOperatorFileRouteContext context, CancellationToken cancellationToken)
    {
        if (!await HasCurrentFilePermissionAsync(context, cancellationToken).ConfigureAwait(false))
            throw new McpOperatorAdmissionRejectedException("permission_denied");
        return await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
    }

    private sealed record McpOperatorFileDirectCaller(ClaimsPrincipal User, IEffectiveAccessService Access);

    private sealed record McpOperatorFileActor(McpOperatorDelegationIdentity Identity, string ServicePrincipal,
        string Resource, string CorrelationId, string RequestId, McpOperatorFileDirectCaller? DirectCaller);
}

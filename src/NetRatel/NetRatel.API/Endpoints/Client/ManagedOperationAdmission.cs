using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Middleware;
using NetRatel.API.Gateway;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Security.Integration;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Conjoins current application authority with MCP policy. A gateway's service
/// token is never an application actor: only the verified delegated account is.
/// </summary>
internal sealed partial class ManagedOperationAdmission(
    IEffectiveAccessService access,
    IApplicationPrincipalResolver principals,
    IMcpOperatorAuthorization authority,
    IMcpOperatorRouteAdmission routes,
    OrchestratorDbContext db,
    IClientPresenceReadModel presence,
    IHostEnvironment environment)
{
    public async Task<ManagedOperationAdmissionResult> EvaluateAsync(ManagedOperationDescriptor descriptor,
        JsonElement request, HttpContext http, CancellationToken cancellationToken)
    {
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var runtime, out var instance))
            return Denied("environment_not_supported");
        var tenantId = request.GetProperty("tenantId").GetInt32();
        Guid? agentId = descriptor.AgentTarget ? request.GetProperty("agentId").GetGuid() : null;
        var actor = await ResolveActorAsync(descriptor, tenantId, agentId, instance, http, cancellationToken).ConfigureAwait(false);
        if (actor is null)
            return Denied("delegated_identity_invalid");
        if (!await access.AuthorizeAsync(actor.User, descriptor.Permission, tenantId, cancellationToken).ConfigureAwait(false))
            return Denied("permission_denied");
        foreach (var permission in descriptor.AdditionalPermissions)
            if (!await access.AuthorizeAsync(actor.User, permission, tenantId, cancellationToken).ConfigureAwait(false))
                return Denied("permission_denied");

        var identity = actor.Identity ?? PermissionBoundIdentity(actor.User, descriptor);
        if (identity is null)
            return Denied("delegated_identity_invalid");
        var principal = new McpOperatorPrincipal(identity.Subject, identity.ClientId, identity.AuthorizedParty,
            identity.Groups.ToHashSet(StringComparer.Ordinal), identity.Roles.ToHashSet(StringComparer.Ordinal),
            identity.Scopes.ToHashSet(StringComparer.Ordinal), actor.ServicePrincipal);
        var scopes = new HashSet<string>(StringComparer.Ordinal) { McpOperationAccessScopeNames.Canonical(descriptor.Scope) };
        McpOperatorRouteAccessRequest? route = null;
        McpOperatorDecision decision;
        if (agentId is { } target)
        {
            // This point read never dispatches a client command or blocks behind
            // the client actor. Cached inventory remains readable while offline.
            var snapshot = await presence.GetClientSnapshotAsync(new ClientKey(tenantId, target), cancellationToken).ConfigureAwait(false);
            var capability = descriptor.Tool != "netratel_services" || descriptor.Operation != "control" ||
                http.RequestServices.GetRequiredService<IAgentCommandGatewaySessionRegistry>()
                    .SupportsServiceControl(new ClientKey(tenantId, target));
            route = new McpOperatorRouteAccessRequest(runtime, principal, actor.ServicePrincipal,
                actor.Resource, instance, descriptor.Tool, descriptor.Operation, tenantId, target, scopes,
                actor.CorrelationId, actor.RequestId, snapshot?.Status == ClientPresenceStatus.Online, capability);
            decision = (await routes.EvaluateAsync(route, cancellationToken).ConfigureAwait(false)).Decision;
        }
        else
        {
            var visible = await authority.HasTenantVisibilityAsync(runtime, principal, tenantId, cancellationToken).ConfigureAwait(false);
            var exists = visible && await db.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == tenantId, cancellationToken).ConfigureAwait(false);
            var operation = McpOperatorOperationCatalog.Find(descriptor.Tool, descriptor.Operation)!;
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"tenant:{tenantId}")));
            decision = await authority.EvaluateAsync(new McpOperatorAccessRequest(runtime, principal, tenantId, null, null,
                operation.OperationFamily, $"{descriptor.Tool}/{descriptor.Operation}", scopes, operation.ConfirmationClass,
                actor.CorrelationId, actor.RequestId, digest, McpResource: actor.Resource, McpInstance: instance,
                Tool: descriptor.Tool, TenantVisible: visible, TargetResolved: exists), cancellationToken).ConfigureAwait(false);
        }
        if (!decision.IsAllowed)
            return Denied(decision.FailureCode ?? "policy_denied");
        var admitted = new ManagedOperationAdmissionResult(actor.User, decision, route, actor.ServicePrincipal, null);
        if (descriptor.Tool == "netratel_ai")
            return await AdmitAiContextAsync(descriptor, request, admitted, cancellationToken).ConfigureAwait(false);
        return IsOfflineMonitoringOperation(descriptor)
            ? await AdmitOfflineOccurrenceAsync(request, admitted, cancellationToken).ConfigureAwait(false)
            : admitted;
    }

    public async Task RecordAcceptedAsync(ManagedOperationAdmissionResult admitted, CancellationToken cancellationToken)
    {
        if (admitted.OfflineOccurrence is { } occurrence &&
            !await OfflineOccurrenceExistsAsync(occurrence, cancellationToken).ConfigureAwait(false))
            throw new McpOperatorAdmissionRejectedException("monitoring_occurrence_unavailable");
        // Context is immutable after investigation creation, but its current
        // client membership, profile and policy can change between review and
        // dispatch. Re-resolve every target before recording any acceptance.
        foreach (var contextRoute in admitted.ContextRoutes)
        {
            var current = await routes.EvaluateAsync(contextRoute, cancellationToken).ConfigureAwait(false);
            if (!current.Decision.IsAllowed)
                throw new McpOperatorAdmissionRejectedException(current.Decision.FailureCode ?? "policy_denied");
        }
        foreach (var contextRoute in admitted.ContextRoutes)
            await routes.RecordAcceptedAsync(contextRoute, cancellationToken).ConfigureAwait(false);
        if (admitted.Route is { } route)
            await routes.RecordAcceptedAsync(route, cancellationToken).ConfigureAwait(false);
        else
            await authority.RecordAcceptedAsync(admitted.Decision!, admitted.ServicePrincipal!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ManagedOperationActor?> ResolveActorAsync(ManagedOperationDescriptor descriptor, int tenantId,
        Guid? agentId, string instance, HttpContext http, CancellationToken cancellationToken)
    {
        if (http.TryGetMcpOperatorDelegation(out var delegation))
        {
            if (delegation!.Tool != descriptor.Tool || delegation.Operation != descriptor.Operation ||
                delegation.TenantId != tenantId || delegation.AgentId != agentId || delegation.Instance != instance ||
                string.IsNullOrWhiteSpace(delegation.Resource) || delegation.ObjectReference is not null)
                return null;

            ClaimsPrincipal user;
            if (delegation.IngressCredentialId is { Length: > 0 } credentialId)
            {
                // The delegation middleware has rechecked this exact current
                // credential, owner, resource and permission for this request.
                if (delegation.IngressPermission != descriptor.Permission)
                    return null;
                user = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("netratel_principal_id", delegation.Identity.Subject),
                    new Claim(ClaimTypes.NameIdentifier, delegation.Identity.Subject),
                    new Claim(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType, credentialId),
                    new Claim("auth_mode", "integration_credential"),
                    new Claim("integration_credential_purpose", "http_mcp")
                ], "McpLocalExecution"));
            }
            else
            {
                if (delegation.Identity.ExternalIssuer is not { Length: > 0 } issuer)
                    return null;
                user = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("iss", issuer),
                    new Claim("sub", delegation.Identity.Subject)
                ], "Oidc"));
                var principalId = await principals.ResolveExternalAsync(user, cancellationToken).ConfigureAwait(false);
                if (principalId is null)
                    return null;
                ((ClaimsIdentity)user.Identity!).AddClaims(
                [
                    new Claim("netratel_principal_id", principalId),
                    new Claim(ClaimTypes.NameIdentifier, principalId)
                ]);
            }
            return new(user, delegation.Identity, delegation.ServicePrincipal, delegation.Resource!,
                delegation.CorrelationId ?? http.TraceIdentifier, delegation.RequestId);
        }

        if (!MonitoringFlowPermissionAuthorization.IsSupportedAccount(http.User) ||
            string.IsNullOrWhiteSpace(http.User.FindFirstValue("netratel_principal_id")))
            return null;
        var direct = http.User.Clone();
        if (direct.FindFirstValue(ClaimTypes.NameIdentifier) is null)
            ((ClaimsIdentity)direct.Identity!).AddClaim(new Claim(ClaimTypes.NameIdentifier, direct.FindFirstValue("netratel_principal_id")!));
        McpOperatorDelegationIdentity? externalIdentity = null;
        if (direct.Identity!.AuthenticationType == "Oidc" &&
            !McpOperatorDelegationIdentityFactory.TryCreate(direct, out externalIdentity))
            return null;
        return new(direct, externalIdentity, "netratel-direct-account", "netratel-api", http.TraceIdentifier, Guid.NewGuid().ToString("N"));
    }

    // Called only after the effective application grant was checked. This is
    // the same permission attenuation used by local HTTP MCP exchange.
    private static McpOperatorDelegationIdentity? PermissionBoundIdentity(ClaimsPrincipal user, ManagedOperationDescriptor descriptor)
    {
        var owner = user.FindFirstValue("netratel_principal_id");
        if (string.IsNullOrWhiteSpace(owner))
            return null;
        return new(owner, user.FindFirstValue(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType),
            "netratel-direct-account", [], McpLocalDelegationPermissionMapper.RolesFor(descriptor.Scope),
            [McpOperationAccessScopeNames.Canonical(descriptor.Scope)]);
    }

    private static ManagedOperationAdmissionResult Denied(string code) => new(null, null, null, null, code);

    private sealed record ManagedOperationActor(ClaimsPrincipal User, McpOperatorDelegationIdentity? Identity,
        string ServicePrincipal, string Resource, string CorrelationId, string RequestId);
}

internal sealed record ManagedOperationAdmissionResult(ClaimsPrincipal? User, McpOperatorDecision? Decision,
    McpOperatorRouteAccessRequest? Route, string? ServicePrincipal, string? FailureCode)
{
    public IReadOnlyList<McpOperatorRouteAccessRequest> ContextRoutes { get; init; } = [];
    public ManagedOfflineOccurrence? OfflineOccurrence { get; init; }
}

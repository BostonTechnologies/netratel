using NetRatel.Shared.Operations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.API.Security.Integration;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace NetRatel.API.Middleware;

/// <summary>
/// Verifies a caller-attribution assertion from the isolated MCP host. This is
/// intentionally separate from API authentication: the API still authenticates
/// the MCP service using its own app token, and no external bearer is relayed.
/// New operator endpoints must explicitly require a value from this middleware.
/// </summary>
public sealed class McpOperatorDelegationMiddleware(
    RequestDelegate next,
    McpOperatorDelegationOptions options,
    McpOperatorDelegationTokenService tokens)
{
    public const string HttpContextItemKey = "netratel.mcp.operator.delegation";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.Enabled)
        {
            await next(context);
            return;
        }

        var headers = context.Request.Headers[McpOperatorDelegationOptions.HeaderName];
        if (headers.Count == 0)
        {
            await next(context);
            return;
        }

        if (headers.Count != 1 || !tokens.TryValidate(headers[0], out var delegation))
        {
            var failure = McpDelegationFailure.ForCode("delegated_identity_invalid")!;
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                code = failure.Code,
                layer = failure.Layer,
                failure,
                correlationId = context.TraceIdentifier
            }, context.RequestAborted);
            return;
        }

        if (!string.IsNullOrWhiteSpace(delegation!.IngressCredentialId))
        {
            var currentCredentials = context.RequestServices.GetService<IIntegrationCredentialCurrentVerifier>();
            var access = context.RequestServices.GetService<IEffectiveAccessService>();
            var objectTargets = context.RequestServices.GetService<IMcpOperationObjectTargetResolver>();
            if (await LocalExecutionFailureAsync(delegation, currentCredentials, access, objectTargets, context.RequestAborted).ConfigureAwait(false) is { } failureCode)
            {
                var failure = McpDelegationFailure.ForCode(failureCode)!;
                context.Response.StatusCode = failureCode == "delegated_identity_invalid" ? StatusCodes.Status401Unauthorized : failure.Layer switch
                {
                    "credential" => StatusCodes.Status401Unauthorized,
                    "input" => StatusCodes.Status400BadRequest,
                    "dependency" => StatusCodes.Status503ServiceUnavailable,
                    _ => StatusCodes.Status403Forbidden
                };
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false, summary = "Local HTTP MCP execution was not admitted.",
                    code = failure.Code, layer = failure.Layer, failure,
                    correlationId = delegation.CorrelationId ?? context.TraceIdentifier
                }, context.RequestAborted);
                return;
            }
        }
        else if ((delegation.Tool is "netratel_files" or "netratel_connectivity" ||
                  delegation.Tool == "netratel_job_runs" && delegation.Operation == "start") &&
            !await IsAuthorizedExternalApplicationExecutionAsync(delegation, context.RequestServices, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                code = delegation.Tool == "netratel_files" ? "delegated_file_permission_denied" : "application_permission_denied",
                layer = "application_permission",
                failure = McpDelegationFailure.ForCode("application_permission_denied"),
                correlationId = delegation.CorrelationId ?? context.TraceIdentifier
            }, context.RequestAborted);
            return;
        }

        context.Items[HttpContextItemKey] = delegation;
        await next(context);
    }

    private static async Task<string?> LocalExecutionFailureAsync(
        McpOperatorDelegation delegation,
        IIntegrationCredentialCurrentVerifier? currentCredentials,
        IEffectiveAccessService? access,
        IMcpOperationObjectTargetResolver? objectTargets,
        CancellationToken cancellationToken)
    {
        if (currentCredentials is null || access is null)
            return "local_credential_exchange_unavailable";
        var current = await currentCredentials.CheckCurrentAsync(delegation.IngressCredentialId!,
            IntegrationCredentialPurpose.HttpMcp, cancellationToken).ConfigureAwait(false);
        if (current.Credential is not { } credential)
            return current.FailureCode ?? "delegated_identity_invalid";
        if (credential.OwnerPrincipalId != delegation.Identity.Subject ||
            credential.CredentialId != delegation.Identity.ClientId || credential.CredentialId != delegation.IngressCredentialId ||
            !string.Equals(credential.Resource, delegation.Resource, StringComparison.Ordinal))
            return "local_delegation_binding_mismatch";
        if (!McpLocalDelegationTargetRequirements.TryGetAuthorizationTenant(delegation, out var authorizationTenantId))
            return "local_delegation_target_invalid";
        var target = McpOperationTargetCatalog.Find(delegation.Tool, delegation.Operation);
        if ((target?.Model is McpOperationTargetModel.ObjectDerived && delegation.ObjectTargetResolutionEnabled ||
             target?.Model is McpOperationTargetModel.PolicyReference) &&
            (objectTargets is null || !await objectTargets.MatchesDelegationAsync(delegation, cancellationToken).ConfigureAwait(false)))
            return "local_delegation_binding_mismatch";
        if (delegation.IngressPermission != McpLocalDelegationPermissionMapper.RequiredPermission(delegation.Tool, delegation.Operation))
            return "local_credential_permission_denied";

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("netratel_principal_id", credential.OwnerPrincipalId),
            new Claim("netratel_integration_credential_id", credential.CredentialId),
            new Claim("auth_mode", "integration_credential"),
            new Claim("integration_credential_purpose", "http_mcp")
        ], "McpLocalExecution"));
        return await McpLocalDelegationPermissionMapper.AuthorizeAsync(access, principal, delegation.Tool, delegation.Operation,
            authorizationTenantId, cancellationToken).ConfigureAwait(false) ? null : "local_credential_permission_denied";
    }

    private static async Task<bool> IsAuthorizedExternalApplicationExecutionAsync(McpOperatorDelegation delegation,
        IServiceProvider services, CancellationToken cancellationToken)
    {
        if (delegation.Identity.ExternalIssuer is not { Length: > 0 } issuer ||
            !McpLocalDelegationTargetRequirements.TryGetAuthorizationTenant(delegation, out var tenantId) ||
            (delegation.Tool == "netratel_files" && tenantId is not > 0) ||
            services.GetService<IApplicationPrincipalResolver>() is not { } principals ||
            services.GetService<IEffectiveAccessService>() is not { } access)
            return false;
        // Only the signed caller's issuer and subject resolve the account.
        // Neither the relay service token nor OAuth roles supply application grants.
        var identity = new ClaimsIdentity([new Claim("iss", issuer), new Claim("sub", delegation.Identity.Subject)], "Oidc");
        var user = new ClaimsPrincipal(identity);
        var principalId = await principals.ResolveExternalAsync(user, cancellationToken).ConfigureAwait(false);
        if (principalId is null) return false;
        identity.AddClaim(new Claim("netratel_principal_id", principalId));
        return await McpLocalDelegationPermissionMapper.AuthorizeAsync(access, user, delegation.Tool, delegation.Operation,
            tenantId, cancellationToken).ConfigureAwait(false);
    }
}

public static class McpOperatorDelegationHttpContextExtensions
{
    public static bool TryGetMcpOperatorDelegation(this HttpContext context, out McpOperatorDelegation? delegation)
    {
        ArgumentNullException.ThrowIfNull(context);
        delegation = context.Items.TryGetValue(McpOperatorDelegationMiddleware.HttpContextItemKey, out var value)
            ? value as McpOperatorDelegation
            : null;
        return delegation is not null;
    }
}

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
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "delegated_identity_invalid",
                layer = "delegation"
            }, context.RequestAborted);
            return;
        }

        if (!string.IsNullOrWhiteSpace(delegation!.IngressCredentialId))
        {
            var currentCredentials = context.RequestServices.GetService<IIntegrationCredentialCurrentVerifier>();
            var access = context.RequestServices.GetService<IEffectiveAccessService>();
            var objectTargets = context.RequestServices.GetService<IMcpOperationObjectTargetResolver>();
            if (!await IsCurrentLocalExecutionAsync(delegation, currentCredentials, access, objectTargets, context.RequestAborted).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = "delegated_identity_revoked",
                    layer = "delegation"
                }, context.RequestAborted);
                return;
            }
        }

        context.Items[HttpContextItemKey] = delegation;
        await next(context);
    }

    private static async Task<bool> IsCurrentLocalExecutionAsync(
        McpOperatorDelegation delegation,
        IIntegrationCredentialCurrentVerifier? currentCredentials,
        IEffectiveAccessService? access,
        IMcpOperationObjectTargetResolver? objectTargets,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(delegation.IngressCredentialId))
            return true;
        if (currentCredentials is null || access is null)
            return false;
        if (string.IsNullOrWhiteSpace(delegation.IngressPermission) ||
            !McpLocalDelegationTargetRequirements.TryGetAuthorizationTenant(delegation, out var authorizationTenantId))
            return false;
        if (McpOperationTargetCatalog.Find(delegation.Tool, delegation.Operation)?.Model is McpOperationTargetModel.ObjectDerived &&
            delegation.ObjectTargetResolutionEnabled &&
            (objectTargets is null ||
             !await objectTargets.MatchesDelegationAsync(delegation, cancellationToken).ConfigureAwait(false)))
            return false;

        var credential = await currentCredentials.VerifyCurrentAsync(
            delegation.IngressCredentialId,
            IntegrationCredentialPurpose.HttpMcp,
            cancellationToken).ConfigureAwait(false);
        if (credential is null || credential.OwnerPrincipalId != delegation.Identity.Subject ||
            !string.Equals(credential.Resource, delegation.Resource, StringComparison.Ordinal))
        {
            return false;
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("netratel_principal_id", credential.OwnerPrincipalId),
            new Claim("netratel_integration_credential_id", credential.CredentialId),
            new Claim("auth_mode", "integration_credential"),
            new Claim("integration_credential_purpose", "http_mcp")
        ], "McpLocalExecution"));
        return await access.AuthorizeAsync(principal, delegation.IngressPermission, authorizationTenantId, cancellationToken)
            .ConfigureAwait(false);
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

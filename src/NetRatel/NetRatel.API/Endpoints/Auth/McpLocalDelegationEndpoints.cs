using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Security.Integration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Operations;
using NetRatel.Shared.Connectivity;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity.Branding;

namespace NetRatel.API.Endpoints.Auth;

/// <summary>
/// The only API surface allowed to receive a local HTTP MCP bearer. It
/// exchanges that bearer for a short-lived, target-bound delegation assertion;
/// normal API routes never see the ingress credential.
/// </summary>
public static class McpLocalDelegationEndpoints
{
    public const string AccessStartPath = "/api/v2/mcp/local-delegation/access/start";
    public const string AuthenticationPath = "/api/v2/mcp/local-delegation/authenticate";
    public const string ExchangePath = "/api/v2/mcp/local-delegation/exchange";
    public const string PairingHeaderName = "X-NetRatel-Mcp-Pairing";

    public static IEndpointRouteBuilder MapMcpLocalDelegationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/local-delegation")
            .WithTags("MCP local delegation")
            // This purpose-bound exchange also requires a short-lived pairing
            // assertion. It is deliberately not a general API credential flow.
            .ExcludeFromDescription()
            .RequireAuthorization("McpLocalDelegationExchange");

        group.MapPost("/authenticate", (
            HttpContext http,
            McpOperatorDelegationTokenService tokens) =>
        {
            if (!TryPairing(http, tokens, authenticationOnly: true, out var pairing))
                return Results.Unauthorized();

            return Results.Ok(new McpLocalAuthenticationResponse(
                http.User.FindFirstValue("netratel_principal_id")!,
                http.User.FindFirstValue(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType)!));
        });

        // This protected setup response contains a device capability. It is never exposed as an MCP tool.
        if (app.ServiceProvider.GetService<IServiceProviderIsService>()?.IsService(typeof(IntegrationPairingService)) == true)
        group.MapPost("/access/start", async (ReviewedMcpAccessProposal proposal, HttpContext http,
            McpOperatorDelegationTokenService tokens, IntegrationPairingService pairing,
            IDeploymentBrandingService branding, IConfiguration configuration, IHostEnvironment environment, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!TryPairing(http, tokens, authenticationOnly: true, out var proof) || proof is null ||
                (!environment.IsDevelopment() && !environment.IsProduction())) return Results.Unauthorized();
            try
            {
                var origin = HttpHostSetupProtocol.NormalizeWebAddress((await branding.GetEffectiveAsync(ct).ConfigureAwait(false)).SiteUrl.Value).TrimEnd('/');
                var resource = configuration["Integration:HttpMcp:PublicResourceUri"] ?? origin + "/mcp";
                return Results.Ok(await pairing.StartReviewedAsync(proposal, http.User.FindFirstValue("netratel_principal_id")!,
                    http.User.FindFirstValue(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType)!, origin, resource,
                    environment.IsProduction() ? McpOperatorEnvironment.Production : McpOperatorEnvironment.Development, ct).ConfigureAwait(false));
            }
            catch (IntegrationPairingUnavailableException) { return Results.BadRequest(new { code = "reviewed_request_unavailable" }); }
            catch (IntegrationCredentialGrantValidationException) { return Results.BadRequest(new { code = "reviewed_proposal_invalid" }); }
        });

        group.MapPost("/exchange", async (
            HttpContext http,
            McpOperatorDelegationTokenService tokens,
            IEffectiveAccessService access,
            CancellationToken cancellationToken) =>
        {
            if (!TryPairing(http, tokens, authenticationOnly: false, out var pairing) || pairing is null)
            {
                return Denied(http, "local_delegation_pairing_invalid", StatusCodes.Status401Unauthorized);
            }

            var target = McpOperationTargetCatalog.Find(pairing.Tool, pairing.Operation);
            var objectTargets = http.RequestServices.GetService<IMcpOperationObjectTargetResolver>();
            if (target?.Model is McpOperationTargetModel.PolicyReference)
            {
                var resolved = objectTargets is not null && pairing.ObjectReference is { } reference
                    ? await objectTargets.ResolvePolicyTargetAsync(reference, cancellationToken).ConfigureAwait(false) : null;
                if (resolved is null || (pairing.TenantId is not null && pairing.TenantId != resolved.TenantId) ||
                    (pairing.AgentId is not null && pairing.AgentId != resolved.AgentId))
                    return Denied(http, "local_delegation_binding_mismatch", StatusCodes.Status403Forbidden, pairing);
                pairing = pairing with { TenantId = resolved.TenantId, AgentId = resolved.AgentId };
            }
            if (!McpLocalDelegationTargetRequirements.TryGetAuthorizationTenant(pairing, out var authorizationTenantId))
                return Denied(http, "local_delegation_target_invalid", StatusCodes.Status400BadRequest, pairing);
            if (target?.Model is McpOperationTargetModel.ObjectDerived && pairing.ObjectTargetResolutionEnabled &&
                (objectTargets is null ||
                 !await objectTargets.MatchesDelegationAsync(pairing, cancellationToken).ConfigureAwait(false)))
            {
                return Denied(http, "local_delegation_binding_mismatch", StatusCodes.Status403Forbidden, pairing);
            }

            var credentialId = http.User.FindFirstValue(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType);
            var ownerPrincipalId = http.User.FindFirstValue("netratel_principal_id");
            var requiredPermission = McpLocalDelegationPermissionMapper.RequiredPermission(pairing.Tool, pairing.Operation);
            var operationAccess = McpOperationAccessCatalog.Find(pairing.Tool, pairing.Operation);
            if (string.IsNullOrWhiteSpace(requiredPermission) || operationAccess is null)
                return Denied(http, "local_delegation_unsupported_operation", StatusCodes.Status403Forbidden, pairing);
            if (string.IsNullOrWhiteSpace(credentialId) || string.IsNullOrWhiteSpace(ownerPrincipalId) ||
                !await McpLocalDelegationPermissionMapper.AuthorizeAsync(access, http.User, pairing.Tool, pairing.Operation,
                    authorizationTenantId, cancellationToken).ConfigureAwait(false))
            {
                return Denied(http, "local_credential_permission_denied", StatusCodes.Status403Forbidden, pairing);
            }

            var identity = new McpOperatorDelegationIdentity(
                ownerPrincipalId,
                credentialId,
                "netratel-local-http-mcp",
                [],
                McpLocalDelegationPermissionMapper.RolesFor(operationAccess.RequiredScope),
                [McpOperationAccessScopeNames.Canonical(operationAccess.RequiredScope)]);
            var assertion = tokens.Create(identity, new McpOperatorDelegationRequest(
                pairing.Tool,
                pairing.Operation,
                Guid.NewGuid().ToString("N"),
                pairing.Resource,
                pairing.Instance,
                pairing.TenantId,
                pairing.AgentId,
                pairing.CorrelationId)
            {
                ObjectReference = pairing.ObjectReference,
                ObjectTargetResolutionEnabled = pairing.ObjectTargetResolutionEnabled,
                IngressCredentialId = credentialId,
                IngressPermission = requiredPermission
            });
            return Results.Ok(new McpLocalExecutionResponse(assertion));
        });

        return app;
    }

    private static IResult Denied(HttpContext http, string code, int status, McpOperatorDelegation? pairing = null) =>
        Results.Json(new
        {
            success = false,
            summary = "Local HTTP MCP delegation was not admitted.",
            code,
            failure = McpDelegationFailure.ForCode(code),
            requiredPermission = pairing is null ? null : McpLocalDelegationPermissionMapper.RequiredPermission(pairing.Tool, pairing.Operation),
            correlationId = pairing?.CorrelationId ?? http.TraceIdentifier
        }, statusCode: status);

    private static bool TryPairing(HttpContext http, McpOperatorDelegationTokenService tokens, bool authenticationOnly, out McpOperatorDelegation? pairing)
    {
        pairing = null;
        var values = http.Request.Headers[PairingHeaderName];
        if (values.Count != 1 || !tokens.TryValidate(values[0], out var candidate) || candidate is null ||
            !string.Equals(candidate.Identity.ClientId, "netratel-local-gateway", StringComparison.Ordinal) ||
            (authenticationOnly && (!string.Equals(candidate.Tool, "netratel_gateway", StringComparison.Ordinal) ||
                                    !string.Equals(candidate.Operation, "authenticate", StringComparison.Ordinal))) ||
            string.IsNullOrWhiteSpace(candidate.Resource) ||
            !NetRatel.Shared.Connectivity.McpResourceUri.Equivalent(candidate.Resource, http.User.FindFirstValue("integration_credential_resource")))
        {
            return false;
        }

        pairing = candidate;
        return true;
    }

    public sealed record McpLocalAuthenticationResponse(string OwnerPrincipalId, string CredentialId);
    public sealed record McpLocalExecutionResponse(string Assertion);
}

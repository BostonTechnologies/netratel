using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using NetRatel.API.Security.Integration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Endpoints.Auth;

/// <summary>Bounded self-inspection for existing CLI/stdio read-only validation.</summary>
public static class AiAgentSelfStatusEndpoints
{
    public const string Policy = "AiAgentSelfStatus";
    public static IEndpointRouteBuilder MapAiAgentSelfStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/auth/ai-agent/status", (ClaimsPrincipal principal, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var credentialId = principal.FindFirstValue(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType);
            var mode = credentialId is not null ? "integration_credential" : Safe(principal.FindFirstValue("auth_mode")) ??
                (principal.Identities.Any(identity => identity.AuthenticationType == "Oidc") ? "oidc" :
                    principal.FindFirst("client_id") is not null ? "m2m" : "local");
            return Results.Ok(new
            {
                authenticated = true,
                authMode = mode,
                name = credentialId ?? Safe(principal.Identity?.Name),
                identityProvider = credentialId is not null ? "netratel" : Safe(principal.FindFirstValue("identity_provider")) ?? mode,
                groups = SafeClaims(principal.FindAll("groups")),
                roles = SafeClaims(principal.FindAll(ClaimTypes.Role).Concat(principal.FindAll("roles"))),
                credentialId,
                credentialPurpose = credentialId is null ? null : "api"
            });
        }).RequireAuthorization(Policy).WithTags("Authentication");
        return app;
    }

    public static async Task<bool> HasCurrentIntegrationGrantAsync(AuthorizationHandlerContext context)
    {
        if (context.Resource is not HttpContext http ||
            !context.User.HasClaim("auth_mode", "integration_credential") ||
            !context.User.HasClaim("integration_credential_purpose", "api")) return false;
        var id = context.User.FindFirstValue(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType);
        if (id is null) return false;
        var credential = await http.RequestServices.GetRequiredService<IIntegrationCredentialCurrentVerifier>()
            .VerifyCurrentAsync(id, IntegrationCredentialPurpose.Api, http.RequestAborted).ConfigureAwait(false);
        if (credential is null || credential.OwnerPrincipalId != context.User.FindFirstValue("netratel_principal_id") || credential.Resource is not null) return false;
        var access = http.RequestServices.GetRequiredService<IEffectiveAccessService>();
        foreach (var grant in credential.Grants)
            if (await access.AuthorizeAsync(context.User, grant.Permission, grant.TenantId, http.RequestAborted).ConfigureAwait(false)) return true;
        foreach (var permission in credential.InstancePermissions)
            if (await access.AuthorizeAsync(context.User, permission, null, http.RequestAborted).ConfigureAwait(false)) return true;
        return false;
    }

    private static string? Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safe = new string(value.Where(character => !char.IsControl(character)).Take(128).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? null : safe;
    }
    private static string[] SafeClaims(IEnumerable<Claim> claims) => claims.Select(claim => Safe(claim.Value))
        .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.Ordinal).Take(32).Order().ToArray();
}

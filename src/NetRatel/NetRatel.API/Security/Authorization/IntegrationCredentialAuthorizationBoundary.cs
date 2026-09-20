using System.Security.Claims;

namespace NetRatel.API.Security.Authorization;

/// <summary>
/// Ensures an integration credential can never acquire authority merely by
/// reaching a newly added authenticated endpoint. Such credentials must be
/// admitted through a permission-aware policy backed by EffectiveAccess.
/// </summary>
internal static class IntegrationCredentialAuthorizationBoundary
{
    public static bool AllowsDefaultAuthenticatedRoute(ClaimsPrincipal principal) =>
        !principal.HasClaim("auth_mode", "integration_credential");
}

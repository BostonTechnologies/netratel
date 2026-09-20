using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using NetRatel.Infrastructure.Identity;

namespace NetRatel.API.Security.Local;

/// <summary>Adds a stable application principal only after OIDC token validation succeeds.</summary>
public sealed class LocalPrincipalClaimsTransformation(IApplicationPrincipalResolver principals) : IClaimsTransformation
{
    public const string PrincipalIdClaimType = "netratel_principal_id";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = principal.Identities.OfType<ClaimsIdentity>().FirstOrDefault(candidate =>
            candidate.IsAuthenticated && string.Equals(candidate.AuthenticationType, "Oidc", StringComparison.Ordinal));
        if (identity is null || identity.HasClaim(claim => claim.Type == PrincipalIdClaimType))
        {
            return principal;
        }

        var principalId = await principals.ResolveExternalAsync(principal).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(principalId))
        {
            identity.AddClaim(new Claim(PrincipalIdClaimType, principalId));
        }

        return principal;
    }
}

using System.Security.Claims;
using NetRatel.Application.Flows;

namespace NetRatel.Infrastructure.Flows;

/// <summary>Rehydrates only the configuring identity; current persisted grants and expiring OIDC facts remain authoritative.</summary>
internal static class FlowAuthorityPrincipal
{
    internal static ClaimsPrincipal Create(FlowExecutionAuthorityDto authority)
    {
        var claims = new List<Claim> { new("netratel_principal_id", authority.PrincipalId) };
        if (authority.IntegrationCredentialId is { } credential)
            claims.Add(new("netratel_integration_credential_id", credential));
        // EffectiveAccessService recognizes this existing server-only identity path. No saved
        // browser roles are copied into a principal, and credential attenuation still applies.
        return new(new ClaimsIdentity(claims, "NetclawSavedAuthority"));
    }
}

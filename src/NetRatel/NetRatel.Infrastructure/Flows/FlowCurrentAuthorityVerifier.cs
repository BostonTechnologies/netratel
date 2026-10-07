using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.Infrastructure.Flows;

public sealed class FlowCurrentAuthorityVerifier(NetRatelIdentityDbContext db, IEffectiveAccessService access) : IFlowExecutionAuthorityVerifier
{
    public async Task<bool> AuthorizeAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default)
    {
        if (tenantId <= 0 || !FlowContractValidation.ValidAuthority(authority) ||
            !await db.ApplicationPrincipals.AnyAsync(principal => principal.Id == authority.PrincipalId, cancellationToken).ConfigureAwait(false) ||
            await db.Users.AnyAsync(user => user.PrincipalId == authority.PrincipalId && !user.IsEnabled, cancellationToken).ConfigureAwait(false)) return false;
        var claims = new List<Claim> { new("netratel_principal_id", authority.PrincipalId) };
        if (authority.IntegrationCredentialId is not null) claims.Add(new("netratel_integration_credential_id", authority.IntegrationCredentialId));
        // Deliberately carries no Operator/group/administrator claim from another principal.
        return await access.AuthorizeAsync(new(new ClaimsIdentity(claims, "FlowDispatch")), NetRatelPermissions.FlowExecute, tenantId, cancellationToken).ConfigureAwait(false);
    }
}

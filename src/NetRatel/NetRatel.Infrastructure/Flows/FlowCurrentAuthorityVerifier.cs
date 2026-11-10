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

        // Deliberately carries no Operator/group/administrator claim from another principal.
        return await access.AuthorizeAsync(FlowAuthorityPrincipal.Create(authority), NetRatelPermissions.FlowExecute, tenantId, cancellationToken).ConfigureAwait(false);
    }
}

using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.Infrastructure.RatelDesk;

public sealed class RatelDeskConnectorAuthorization(IEffectiveAccessService access, NetRatelIdentityDbContext identity)
    : IRatelDeskConnectorAuthorization
{
    public async Task<bool> CanManageAsync(ClaimsPrincipal principal, int tenantId, CancellationToken cancellationToken)
    {
        var id = principal.FindFirst("netratel_principal_id")?.Value;
        if (string.IsNullOrWhiteSpace(id) || !await IsCurrentPrincipalAsync(id, cancellationToken).ConfigureAwait(false)) return false;
        return await access.AuthorizeAsync(principal, NetRatelPermissions.IntegrationManagement, tenantId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CanExecuteAsync(string principalId, string? integrationCredentialId, int tenantId, CancellationToken cancellationToken)
    {
        if (tenantId <= 0 || !await IsCurrentPrincipalAsync(principalId, cancellationToken).ConfigureAwait(false)) return false;
        var claims = new List<Claim> { new("netratel_principal_id", principalId) };
        if (integrationCredentialId is not null) claims.Add(new("netratel_integration_credential_id", integrationCredentialId));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "PersistedFlowAuthority"));
        return await access.AuthorizeAsync(principal, NetRatelPermissions.IntegrationManagement, tenantId, cancellationToken).ConfigureAwait(false) &&
            await access.AuthorizeAsync(principal, NetRatelPermissions.SecretUse, tenantId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsCurrentPrincipalAsync(string id, CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(id) && await identity.ApplicationPrincipals.AsNoTracking().AnyAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false) &&
        !await identity.Users.AsNoTracking().AnyAsync(user => user.PrincipalId == id && !user.IsEnabled, cancellationToken).ConfigureAwait(false);
}

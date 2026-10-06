using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

/// <summary>Adopts the existing Flow singleton through the foundation's human-authorized identity operation.</summary>
public sealed class RatelDeskConnectorSetupService(IRatelDeskConnectorAuthorization authorization,
    IFlowSourceIdentityResolver flow, ServiceLinkIdentityStore identity, ServiceLinkProfileService profiles,
    OrchestratorDbContext db) : IRatelDeskConnectorSetupService
{
    public async Task<RatelDeskConnectorSetupDto> GetAsync(int tenantId, ClaimsPrincipal actor, CancellationToken ct)
    {
        await RequireAsync(tenantId, actor, ct);
        // Apply deployment identity before creating an absent Flow producer; existing Flow IDs always win.
        var installed = await identity.GetAsync(ct);
        var source = await flow.EnsureAsync(ct);
        installed = await identity.GetAsync(ct);
        var links = new List<RatelDeskManagedLinkOptionDto>();
        var tenant = tenantId.ToString(CultureInfo.InvariantCulture);
        var ids = await db.Set<ServiceLinkAttempt>().AsNoTracking()
            .Where(x => x.LocalTenantId == tenant && x.LinkId != null && x.LocalBusinessSenderEnabled)
            .OrderBy(x => x.LinkId).Select(x => x.LinkId!).Take(100).ToListAsync(ct);
        foreach (var id in ids)
        {
            try
            {
                var profile = await profiles.ResolveAsync(tenantId, id, "rateldesk.incidents.create", ct);
                if (profile.SourceInstanceId != source.ToString("D") ||
                    !profile.Grant.Scopes.Contains("rateldesk.incident-receipts.read", StringComparer.Ordinal) ||
                    !profile.Grant.Scopes.Contains("rateldesk.incident-targets.read", StringComparer.Ordinal) ||
                    profile.Grant.ResourceConstraints.CustomerIds.Length != 1) continue;
                links.Add(new(id, profile.PeerInstanceId, profile.Peer.ApiBaseUrl,
                    profile.Grant.ResourceConstraints.OrganizationId!, profile.Grant.ResourceConstraints.CustomerIds[0], profile.LinkRevision));
            }
            catch (ServiceLinkProtocolException) { /* Denied/incomplete profiles are not selectable. No discovery or token HTTP. */ }
        }
        return new(source, installed.InstanceId, installed.SourceInstanceId, installed.Revision, links);
    }

    public async Task<RatelDeskConnectorSetupDto> AdoptAsync(int tenantId, long expectedIdentityRevision, ClaimsPrincipal actor, CancellationToken ct)
    {
        await RequireAsync(tenantId, actor, ct);
        if (actor.Identity?.IsAuthenticated != true || actor.FindFirst("netratel_integration_credential_id") is not null ||
            actor.FindFirst(ServiceIdentityClaims.PrincipalId) is not null || actor.HasClaim("auth_mode", "service") ||
            actor.HasClaim("auth_mode", "machine_token"))
            throw new UnauthorizedAccessException("interactive-administrator-required");
        _ = await identity.GetAsync(ct);
        var source = await flow.EnsureAsync(ct);
        // Also requires the foundation's current instance integration-management human authority.
        // A previous distinct producer remains a conflict; this never replaces or aliases it.
        _ = await identity.AdoptSourceAsync(actor, source, expectedIdentityRevision, ct);
        return await GetAsync(tenantId, actor, ct);
    }

    private async Task RequireAsync(int tenantId, ClaimsPrincipal actor, CancellationToken ct)
    {
        if (tenantId <= 0 || !await authorization.CanManageAsync(actor, tenantId, ct))
            throw new UnauthorizedAccessException("connector-management-required");
    }
}

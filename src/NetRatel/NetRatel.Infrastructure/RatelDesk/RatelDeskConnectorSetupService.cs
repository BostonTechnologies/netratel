using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using NetRatel.Shared.ServiceLinks;
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
    OrchestratorDbContext db, IRatelDeskConnectorStore? connectors = null, RatelDeskConnectorReceiver? receiver = null) : IRatelDeskConnectorSetupService
{
    public async Task PrepareProducerAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        if (actor.Identity?.IsAuthenticated != true || actor.FindFirst("netratel_integration_credential_id") is not null ||
            actor.FindFirst(ServiceIdentityClaims.PrincipalId) is not null || actor.HasClaim("auth_mode", "service") || actor.HasClaim("auth_mode", "machine_token"))
            throw new UnauthorizedAccessException("interactive-administrator-required");
        var installed = await identity.GetAsync(ct);
        // First installation setup is explicitly human-authorized before the persistent Flow singleton is created.
        if (installed.SourceInstanceId is null) await identity.AuthorizeAdoptionAsync(actor, ct);
        var source = await flow.EnsureAsync(ct);
        if (installed.SourceInstanceId == source.ToString("D")) return;
        // The same protected adoption/CAS owner used by AdoptAsync; never replaces an existing producer or approved attempt.
        _ = await identity.AdoptSourceAsync(actor, source, installed.Revision, ct);
    }

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
            catch (ServiceLinkProtocolException) { continue; }
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

    public async Task<RatelDeskConnectionCompletionDto> CompleteAsync(int tenantId, string linkId, ClaimsPrincipal actor, CancellationToken ct)
    {
        await RequireAsync(tenantId, actor, ct);
        return await CompleteApprovedAsync(tenantId, linkId, ct);
    }

    public async Task<bool> IsApprovedReferenceReadyAsync(int tenantId, string linkId, CancellationToken ct)
    {
        if (connectors is null || receiver is null) return false;
        var connector = (await connectors.ListAsync(tenantId, ct)).FirstOrDefault(x => x.Configuration.Enabled &&
            x.Authentication?.Mode == RatelDeskAuthenticationMode.ManagedServiceLink && x.Authentication.ManagedLinkId == linkId);
        return connector is not null && (await receiver.CurrentAsync(connector, ct)).Available;
    }

    public async Task EnsureApprovedReferenceAsync(int tenantId, string linkId, CancellationToken ct)
    {
        if (connectors is null) return;
        // Background upkeep creates an absent reference once. Fresh probes remain explicit after the first completion.
        if ((await connectors.ListAsync(tenantId, ct)).Any(x =>
            x.Authentication?.Mode == RatelDeskAuthenticationMode.ManagedServiceLink && x.Authentication.ManagedLinkId == linkId)) return;
        _ = await CompleteApprovedAsync(tenantId, linkId, ct);
    }

    public async Task<RatelDeskConnectionCompletionDto> CompleteApprovedAsync(int tenantId, string linkId, CancellationToken ct)
    {
        ServiceLinkValidation.Id(linkId);
        var tenant = tenantId.ToString(CultureInfo.InvariantCulture);
        var attempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleOrDefaultAsync(x => x.LinkId == linkId && x.LocalTenantId == tenant, ct);
        if (attempt is null || attempt.LifecycleState != "active" || !attempt.LocalBusinessSenderEnabled || connectors is null || receiver is null)
            throw new ServiceLinkProtocolException(409, "connection-pending", "Finish approving the connection before enabling incident delivery.");
        // The immutable human consent supplies its durable owner; current permission checks still apply after sign-out.
        if (!await authorization.CanExecuteAsync(attempt.LocalActorId, null, tenantId, ct))
            throw new UnauthorizedAccessException("connector-owner-denied");
        var profile = await profiles.ResolveAsync(tenantId, linkId, "rateldesk.incidents.create", ct);
        var producer = await flow.EnsureAsync(ct);
        if (profile.SourceInstanceId != producer.ToString("D") || profile.Grant.ResourceConstraints.CustomerIds.Length != 1 ||
            !profile.Grant.Scopes.Contains("rateldesk.incident-receipts.read", StringComparer.Ordinal) ||
            !profile.Grant.Scopes.Contains("rateldesk.incident-targets.read", StringComparer.Ordinal))
            throw new ServiceLinkProtocolException(409, "incident-grant-incomplete", "The approved connection lacks the incident target and receipt permissions.");
        var existing = (await connectors.ListAsync(tenantId, ct)).FirstOrDefault(x =>
            x.Authentication?.Mode == RatelDeskAuthenticationMode.ManagedServiceLink && x.Authentication.ManagedLinkId == linkId);
        if (existing is null)
        {
            // One stable reference per approved link. Identical completion/recovery cannot create another connector.
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes("NetRatel.RatelDesk.Connection/" + tenant + "/" + linkId));
            var id = new Guid(digest.AsSpan(0, 16));
            var name = "RatelDesk · " + new Uri(profile.Peer.WebBaseUrl).Host;
            var configuration = new RatelDeskConnectorConfiguration(name, profile.Peer.ApiBaseUrl,
                profile.Grant.ResourceConstraints.OrganizationId!, profile.Grant.ResourceConstraints.CustomerIds[0], null, [], new(), true);
            existing = new(id, tenantId, 1, 1, attempt.LocalActorId, configuration, null, 0,
                new(RatelDeskAuthenticationMode.ManagedServiceLink, linkId));
            if (!await connectors.SaveAsync(existing, 0, ct))
                existing = await connectors.GetAsync(tenantId, id, ct) ?? throw new InvalidOperationException("connector-conflict");
        }
        if (existing.Configuration.OrganizationId != profile.Grant.ResourceConstraints.OrganizationId ||
            existing.Configuration.CustomerId != profile.Grant.ResourceConstraints.CustomerIds[0] || existing.Configuration.Origin != profile.Peer.ApiBaseUrl)
            throw new ServiceLinkProtocolException(409, "connector-mapping-conflict", "Review the existing connector mapping before completing this connection.");
        var current = await receiver.CurrentAsync(existing, ct);
        var result = current.Available
            ? new RatelDeskConnectionTestResult(RatelDeskConnectionTestStatus.MappingValidated, current.Code, true)
            : await receiver.TestAsync(existing, ct);
        var observed = (await connectors.GetAsync(tenantId, existing.Id, ct))?.Readiness?.Capability;
        return new(existing.Id, existing.Configuration.Name, result.AutomaticDeliveryAvailable, result.Code)
        { OrganizationName = observed?.OrganizationName, CustomerName = observed?.CustomerName };
    }

    private async Task RequireAsync(int tenantId, ClaimsPrincipal actor, CancellationToken ct)
    {
        if (tenantId <= 0 || !await authorization.CanManageAsync(actor, tenantId, ct))
            throw new UnauthorizedAccessException("connector-management-required");
    }
}

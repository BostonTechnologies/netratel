using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using static NetRatel.Infrastructure.ServiceLinks.ServiceLinkValidation;

namespace NetRatel.Infrastructure.ServiceLinks;

public sealed partial class ServiceLinkCoordinator
{
    private static string? ActorId(ClaimsPrincipal actor) => actor.FindFirstValue("netratel_principal_id") ??
        actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub");

    private Task<bool> LocalTenantExists(string tenantId, CancellationToken ct) =>
        ServiceLinkGrantAuthority.TryTenant(tenantId, out var tenant)
            ? db.Tenants.AsNoTracking().AnyAsync(x => x.Id == tenant, ct) : Task.FromResult(false);

    private async Task<bool> IsUnconsentedUnprepared(ServiceLinkAttempt a, CancellationToken ct) =>
        a.Decision is "undecided" or "abort" && a.LifecycleState is "awaiting_approval" or "expired" or "failed" &&
        a.LinkId is null && a.GrantSummaryJson is null && a.GrantHash is null && a.ConsentId is null && a.CommitId is null &&
        a.InboundPrincipalId is null && a.ProtectedInboundEscrow is null && a.ProtectedOutboundCredential is null && a.OutboundProfileRevision is null &&
        !a.ExchangeDispatched && a.ExchangeFingerprint is null && a.ExchangeResponseHash is null && a.ProtectedExchangeResponse is null &&
        a.PairingCodeHash is null && a.ProtectedPairingCode is null && !a.LocalPreparedAcknowledged && !a.PeerPreparedAcknowledged &&
        !a.LocalInboundActive && !a.LocalBusinessSenderEnabled && !a.PeerActiveAcknowledged && !a.LocalActiveAcknowledged &&
        RetainedDescriptorMatches(a) &&
        !await db.Set<ServiceLinkOperation>().AsNoTracking().AnyAsync(o => o.LinkId == a.AttemptId || o.LinkId == a.LinkId, ct) &&
        !await db.Set<ServicePrincipalRegistration>().AsNoTracking().AnyAsync(p => p.AttemptId == a.AttemptId, ct) &&
        !await db.Set<ServiceLinkVerificationReceipt>().AsNoTracking().AnyAsync(r => r.AttemptId == a.AttemptId, ct);

    private static bool RetainedDescriptorMatches(ServiceLinkAttempt a)
    {
        try
        {
            var descriptor = Descriptor(a);
            return descriptor.AttemptId == a.AttemptId && descriptor.DescriptorHash == a.DescriptorHash &&
                ServiceLinkPayloadNormalization.DescriptorHashMatches(descriptor, a.DescriptorHash) &&
                (a.Role != "responder" || (descriptor.RequestedResponderTenantId ?? "") == a.LocalTenantId);
        }
        catch (JsonException) { return false; }
    }

    // Only inspection and cancellation may bypass an invalid, never-consented binding.
    // Consent, resume, rotation and prepared/committed recovery keep their tenant gate.
    private async Task AuthorizeInspectionOrCancellation(ServiceLinkAttempt a, ClaimsPrincipal actor, CancellationToken ct)
    {
        if (await LocalTenantExists(a.LocalTenantId, ct)) { await AuthorizeAttempt(a, actor, ct); return; }
        var tenants = await accessService.GetAuthorizedTenantIdsAsync(actor, NetRatelPermissions.IntegrationManagement, ct);
        Require(actor.Identity?.IsAuthenticated == true && actor.FindFirst("netratel_integration_credential_id") is null &&
            actor.FindFirst(ServiceIdentityClaims.PrincipalId) is null && !string.IsNullOrEmpty(a.LocalActorId) &&
            a.LocalActorId == ActorId(actor) && a.Role == "responder" &&
            (string.IsNullOrEmpty(a.LocalTenantId) ? tenants is null || tenants.Length > 0 : tenants is null) &&
            await IsUnconsentedUnprepared(a, ct), "administrator-required",
            "The original local administrator must inspect or cancel this unapproved attempt with current authority.", 403);
    }

    private string AvailableAction(ServiceLinkAttempt a, ClaimsPrincipal actor, bool invalidBinding, bool terminal)
    {
        if (invalidBinding || terminal) return "none";
        if (CanReturnApproval(a)) return a.LocalActorId == ActorId(actor) ? "return" : "none";
        if (a.Decision == "undecided" && a.InboundPrincipalId is null)
        {
            if (a.LocalActorId != ActorId(actor) || a.ProtectedBrowserState is null) return "none";
            if (a.Role == "responder" && a.GrantSummaryJson is null && a.LifecycleState == "awaiting_approval") return "respond";
            if (a.Role == "initiator" && a.ProtectedVerifier is not null && a.GrantSummaryJson is null && a.LifecycleState == "awaiting_approval") return "continue";
            if (a.Role == "initiator" && a.GrantSummaryJson is not null && a.LifecycleState == "approved") return "review";
            return "none";
        }
        return a.LifecycleState == "active" ? "none" : "resume";
    }
}

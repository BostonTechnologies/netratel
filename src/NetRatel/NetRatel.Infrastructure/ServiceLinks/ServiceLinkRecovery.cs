using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using static NetRatel.Infrastructure.ServiceLinks.ServiceLinkValidation;

namespace NetRatel.Infrastructure.ServiceLinks;

public sealed partial class ServiceLinkCoordinator
{
    // A retained relationship remains exclusive through recovery and cancellation until
    // the lifecycle coordinator releases its key. Never replace or silently remap consent.
    private async Task RequireAvailableRelationship(string localTenant, string peerInstance, string? peerTenant,
        string? currentAttempt, ClaimsPrincipal actor, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(peerTenant)) return;
        await Authorize(actor, localTenant, ct);
        var key = Digest(localTenant + "\n" + peerInstance + "\n" + peerTenant);
        var existing = await db.Set<ServiceLinkAttempt>().AsNoTracking()
            .Where(x => x.ActiveRelationshipKey == key && x.LocalTenantId == localTenant && x.PeerInstanceId == peerInstance &&
                x.PeerTenantId == peerTenant && x.AttemptId != currentAttempt)
            .Select(x => x.AttemptId).SingleOrDefaultAsync(ct);
        if (existing is not null) throw ExistingRelationship(existing);
    }

    private static ServiceLinkProtocolException ExistingRelationship(string attemptId) =>
        new(409, "relationship-already-exists", "Use the existing locally authorized connection or setup.") { ExistingAttemptId = attemptId };

    private bool CanReturnApproval(ServiceLinkAttempt a) => a.Role == "responder" && a.Decision == "undecided" &&
        a.LifecycleState == "approved" && a.ExpiresAtUnixSeconds > Now && a.GrantSummaryJson is not null && a.GrantHash is not null &&
        a.InboundPrincipalId is not null && a.ProtectedBrowserState is not null && a.ProtectedPairingCode is not null && a.PairingCodeHash is not null &&
        a.ExchangeFingerprint is null && !a.ExchangeDispatched && a.ProtectedOutboundCredential is null;
}

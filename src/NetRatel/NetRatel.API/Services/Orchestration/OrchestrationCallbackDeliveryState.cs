using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services.Orchestration;

/// <summary>One phase lease shared by automatic delivery and explicit operator replay.</summary>
internal static class OrchestrationCallbackDeliveryState
{
    internal static async Task<(OrchestrationCallbackDelivery Delivery, Guid LeaseId)?> TryLeaseAsync(
        OrchestratorDbContext db, int requestId, string phase, DateTimeOffset now, bool replay, CancellationToken ct)
    {
        var delivery = await db.Set<OrchestrationCallbackDelivery>()
            .SingleOrDefaultAsync(x => x.RequestId == requestId && x.Phase == phase, ct);
        if (delivery is null)
        {
            delivery = new() { RequestId = requestId, Phase = phase, NextAttemptAtUtc = now };
            db.Add(delivery);
            await db.SaveChangesAsync(ct);
        }
        if (delivery.DeliveredAtUtc is not null || !replay &&
                (delivery.RejectedAtUtc is not null || delivery.NextAttemptAtUtc > now) ||
            delivery.LeaseExpiresAtUtc is { } expiry && expiry > now) return null;

        var leaseId = Guid.NewGuid();
        delivery.LeaseId = leaseId;
        delivery.LeaseExpiresAtUtc = now.AddMinutes(3);
        delivery.Attempts++;
        delivery.Revision++;
        await db.SaveChangesAsync(ct);
        return (delivery, leaseId);
    }

    internal static async Task CompleteAsync(OrchestratorDbContext db, OrchestrationCallbackDelivery delivery,
        Guid leaseId, OrchestrationCallbackResult result, DateTimeOffset now, CancellationToken ct)
    {
        await db.Entry(delivery).ReloadAsync(ct);
        if (delivery.LeaseId != leaseId) return;
        delivery.LeaseId = null;
        delivery.LeaseExpiresAtUtc = null;
        delivery.NextAttemptAtUtc = now.AddSeconds(15);
        delivery.DeliveredAtUtc = result.Disposition == OrchestrationCallbackDisposition.Delivered ? now : null;
        delivery.RejectedAtUtc = result.Disposition == OrchestrationCallbackDisposition.Rejected ? now : null;
        delivery.LastStatusCode = result.StatusCode;
        delivery.LastFailureCode = result.FailureCode;
        delivery.Revision++;
        await db.SaveChangesAsync(ct);
    }
}

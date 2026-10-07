using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.RatelDesk;

public static class ReceiverDispatchPolicy
{
    public static FlowIncidentActionResult Complete(RatelDeskDispatchEvidence evidence,
        RatelDeskReceiverObservation observation, DateTimeOffset now)
    {
        if (observation.Kind == RatelDeskReceiverObservationKind.Committed && observation.Receipt is { } receipt)
            return new(FlowIncidentActionResultKind.Succeeded, "incident-receipt-verified",
                new FlowActionReceiptDto(receipt.IncidentId, receipt.TrackingId, null));
        var withinBudget = now < evidence.Prepared.AutomaticReplayUntilUtc &&
            evidence.Attempts < FlowLimits.MaximumActionAttempts;
        var uncertain = evidence.MayHaveCommitted || observation.Kind == RatelDeskReceiverObservationKind.PossibleCommit;
        // The persisted flag permits one final receipt read; it cannot admit another POST.
        if (uncertain && evidence.Attempts == FlowLimits.MaximumActionAttempts &&
            evidence.FinalReconciliationPending && now < evidence.Prepared.AutomaticReplayUntilUtc &&
            observation.Kind != RatelDeskReceiverObservationKind.Gone)
            return new(FlowIncidentActionResultKind.RetryableSafe, "receiver-final-reconciliation-required",
                RetryAfter: observation.RetryAfter ?? TimeSpan.FromSeconds(5));
        if (observation.Kind == RatelDeskReceiverObservationKind.Gone || !withinBudget)
            return new(uncertain ? FlowIncidentActionResultKind.DeliveryUnknown : FlowIncidentActionResultKind.Failed,
                uncertain ? "delivery-horizon-exhausted-unknown" : "action-budget-exhausted");
        if (observation.Kind is RatelDeskReceiverObservationKind.RateLimited or RatelDeskReceiverObservationKind.TransientReadFailure or
            RatelDeskReceiverObservationKind.PossibleCommit or RatelDeskReceiverObservationKind.Missing)
            return new(FlowIncidentActionResultKind.RetryableSafe, observation.Code,
                RetryAfter: observation.RetryAfter ?? TimeSpan.FromSeconds(5));
        // Current denial or malformed selection cannot erase an earlier possible commit.
        return new(uncertain ? FlowIncidentActionResultKind.DeliveryUnknown : FlowIncidentActionResultKind.Failed, observation.Code);
    }

    public static FlowActionStatus BudgetExhausted(bool mayHaveCommitted) =>
        mayHaveCommitted ? FlowActionStatus.DeliveryUnknown : FlowActionStatus.Failed;
}

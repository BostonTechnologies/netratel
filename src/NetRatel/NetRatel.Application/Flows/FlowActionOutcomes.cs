using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

public static class FlowActionOutcomes
{
    public static FlowRuntimeResult Aggregate(IReadOnlyList<FlowIncidentActionResult> results)
    {
        if (results.Any(result => result.Kind == FlowIncidentActionResultKind.Waiting))
            return new(results.Any(result => result.AiResult?.Status == FlowAiResultStatus.AwaitingApproval)
                ? FlowRunStatus.AwaitingApproval : FlowRunStatus.WaitingForAi, "ai-investigation-waiting");
        // Retriable siblings remain runnable even when another destination failed or is uncertain.
        if (results.Any(result => result.Kind == FlowIncidentActionResultKind.RetryableSafe))
            return new(FlowRunStatus.RetryWaiting, "actions-retry-waiting");
        var successes = results.Count(result => result.Kind == FlowIncidentActionResultKind.Succeeded);
        if (successes == results.Count && successes > 0) return new(FlowRunStatus.Succeeded, "execution-completed");
        if (successes > 0) return new(FlowRunStatus.PartiallySucceeded, "actions-partially-succeeded");
        if (results.Any(result => result.Kind == FlowIncidentActionResultKind.DeliveryUnknown))
            return new(FlowRunStatus.DeliveryUnknown, "delivery-unknown");
        return new(FlowRunStatus.Failed, results.FirstOrDefault()?.Code ?? "action-not-reached");
    }
}

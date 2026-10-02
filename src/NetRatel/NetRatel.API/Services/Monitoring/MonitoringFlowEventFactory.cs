using NetRatel.Application.Flows;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.API.Services.Monitoring;

/// <summary>The immutable persisted intent is the only source of ingress bytes.</summary>
public static class MonitoringFlowEventFactory
{
    public static FlowEventEnvelope? Create(MonitoringOutboxIntent intent)
    {
        if (!MonitoringContractValidator.TryValidateRule(intent.PinnedRule, out _) ||
            intent.Series.TenantId != intent.PinnedRule.TenantId || intent.Series.RuleId != intent.PinnedRule.RuleId ||
            intent.PublishedFlowVersionId != intent.PinnedRule.PublishedFlowVersionId ||
            intent.PinnedRule.ExecutionPrincipalId is not { } principal ||
            intent.StableFlowDispatchKey != $"monitoring:v1:{intent.Series.TenantId}:{intent.OccurrenceId:D}:{intent.EventId:D}:{intent.PublishedFlowVersionId:D}") return null;
        var input = new FlowEventEnvelope(intent.Series.TenantId, intent.EventId, intent.OccurrenceId,
            intent.PublishedFlowVersionId, intent.AtUtc,
            new(intent.Series.AgentId, intent.Series.RuleId, intent.PinnedRule.Name,
                intent.Series.AgentId.ToString("D"), intent.Series.ResourceKey,
                intent.PinnedRule.Condition.Kind.ToString(), intent.PinnedRule.Severity.ToString(),
                intent.PinnedEvidence.NumericValue, intent.PinnedEvidence.ServiceState?.ToString(), intent.PinnedEvidence.ObservedAtUtc),
            new(principal, intent.PinnedRule.ExecutionCredentialId));
        return FlowContractValidation.ValidEnvelope(input) ? input : null;
    }

    public static bool Matches(FlowEventEnvelope input, MonitoringOutboxIntent intent) =>
        Create(intent) is { } canonical && canonical == input;

    public static bool Matches(FlowRunSummaryDto run, MonitoringOutboxIntent intent, Guid? expectedRunId = null) =>
        run.Id != Guid.Empty && (expectedRunId is null || run.Id == expectedRunId) &&
        run.EventId == intent.EventId && run.OccurrenceId == intent.OccurrenceId && run.FlowVersionId == intent.PublishedFlowVersionId;
}

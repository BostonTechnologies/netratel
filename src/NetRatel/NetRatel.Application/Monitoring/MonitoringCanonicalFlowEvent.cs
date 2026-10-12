using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Application.Monitoring;

/// <summary>The immutable V1 intent determines the exact Flow envelope; no admission proof alters those bytes.</summary>
public static class MonitoringCanonicalFlowEvent
{
    public static FlowEventEnvelope? Create(MonitoringOutboxIntent intent)
    {
        if (!MonitoringContractValidator.TryValidateRule(intent.PinnedRule, out _) ||
            intent.Series.TenantId != intent.PinnedRule.TenantId || intent.Series.RuleId != intent.PinnedRule.RuleId ||
            intent.PublishedFlowVersionId != intent.PinnedRule.PublishedFlowVersionId ||
            intent.PinnedRule.ExecutionPrincipalId is not { } principal ||
            intent.StableFlowDispatchKey != $"monitoring:v1:{intent.Series.TenantId}:{intent.OccurrenceId:D}:{intent.EventId:D}:{intent.PublishedFlowVersionId:D}") return null;
        var offline = intent.OfflineSummary ?? intent.PinnedEvidence.OfflineSummary;
        var input = new FlowEventEnvelope(intent.Series.TenantId, intent.EventId, intent.OccurrenceId,
            intent.PublishedFlowVersionId, intent.AtUtc,
            new(intent.Series.AgentId, intent.Series.RuleId, intent.PinnedRule.Name,
                MonitoringOfflinePresentation.IsSeries(intent.Series) ? "Monitored clients" : intent.Series.AgentId.ToString("D"), intent.Series.ResourceKey,
                MonitoringOfflinePresentation.IsSeries(intent.Series) ? "ClientOffline" : intent.PinnedRule.Condition.Kind.ToString(), intent.PinnedRule.Severity.ToString(),
                offline?.OfflineCount ?? intent.PinnedEvidence.NumericValue, intent.PinnedEvidence.ServiceState?.ToString(),
                intent.OfflineSummary is null ? intent.PinnedEvidence.ObservedAtUtc : intent.AtUtc)
                { OfflineSummary = offline },
            new(principal, intent.PinnedRule.ExecutionCredentialId));
        return FlowContractValidation.ValidEnvelope(input) ? input : null;
    }
}

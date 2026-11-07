using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Flows;
using System.Text.Json;

namespace NetRatel.Application.Monitoring;

/// <summary>The immutable intent determines exact Flow bytes. Absent presentation retains the historical V1 envelope.</summary>
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
        if (intent.Presentation is { } presentation && (!MonitoringPresentationSnapshot.IsValid(presentation) ||
            presentation.TenantId != intent.Series.TenantId || presentation.RuleId != intent.Series.RuleId ||
            presentation.AgentId != intent.Series.AgentId || presentation.OccurrenceId != intent.OccurrenceId)) return null;
        var input = new FlowEventEnvelope(intent.Series.TenantId, intent.EventId, intent.OccurrenceId,
            intent.PublishedFlowVersionId, intent.AtUtc,
            new(intent.Series.AgentId, intent.Series.RuleId, intent.PinnedRule.Name,
                intent.Presentation?.ClientName ?? (MonitoringOfflinePresentation.IsSeries(intent.Series) ? "Monitored clients" : intent.Series.AgentId.ToString("D")), intent.Series.ResourceKey,
                MonitoringOfflinePresentation.IsSeries(intent.Series) ? "ClientOffline" : intent.PinnedRule.Condition.Kind.ToString(), intent.PinnedRule.Severity.ToString(),
                offline?.OfflineCount ?? intent.PinnedEvidence.NumericValue, intent.PinnedEvidence.ServiceState?.ToString(),
                intent.OfflineSummary is null ? intent.PinnedEvidence.ObservedAtUtc : intent.AtUtc)
                { OfflineSummary = offline, Presentation = intent.Presentation },
            new(principal, intent.PinnedRule.ExecutionCredentialId));
        if (intent.Presentation is not null) input = FitPresentation(input);
        return FlowContractValidation.ValidEnvelope(input) ? input : null;
    }

    private static FlowEventEnvelope FitPresentation(FlowEventEnvelope input)
    {
        var data = input.Data!;
        var snapshot = data.Presentation!;
        // Presentation is optional additive data; a long but admitted original key/authority must retain
        // its established event budget. Derive the same bounded projection on every retry, using no live data.
        while (JsonSerializer.SerializeToUtf8Bytes(input).Length > FlowLimits.MaximumEventBytes && snapshot.Details.Length > 64)
        {
            snapshot = snapshot with { Details = Cut(snapshot.Details, snapshot.Details.Length - 64) + "\n[Details truncated; see Monitoring.]" };
            input = input with { Data = data with { Presentation = snapshot } };
        }
        if (JsonSerializer.SerializeToUtf8Bytes(input).Length <= FlowLimits.MaximumEventBytes) return input;
        snapshot = snapshot with { ClientUrl = null, AlertUrl = null, Disk = null,
            Details = "Presentation shortened to fit the transport limit. Links unavailable; full pinned evidence remains in Monitoring." };
        input = input with { Data = data with { Presentation = snapshot } };
        for (var attempt = 0; attempt < 5 && JsonSerializer.SerializeToUtf8Bytes(input).Length > FlowLimits.MaximumEventBytes; attempt++)
        {
            snapshot = snapshot with { Title = Cut(snapshot.Title, Math.Max(8, snapshot.Title.Length / 2)),
                ClientName = Cut(snapshot.ClientName, Math.Max(8, snapshot.ClientName.Length / 2)),
                Hostname = snapshot.Hostname is null ? null : Cut(snapshot.Hostname, Math.Max(8, snapshot.Hostname.Length / 2)),
                TenantName = Cut(snapshot.TenantName, Math.Max(8, snapshot.TenantName.Length / 2)),
                FormattedValue = Cut(snapshot.FormattedValue, Math.Max(8, snapshot.FormattedValue.Length / 2)),
                Condition = Cut(snapshot.Condition, Math.Max(8, snapshot.Condition.Length / 2)),
                RecoveryCondition = Cut(snapshot.RecoveryCondition, Math.Max(8, snapshot.RecoveryCondition.Length / 2)) };
            input = input with { Data = data with { ClientName = snapshot.ClientName, Presentation = snapshot } };
        }
        if (JsonSerializer.SerializeToUtf8Bytes(input).Length <= FlowLimits.MaximumEventBytes) return input;
        // An original envelope can occupy almost its entire budget. Retain the full snapshot on its
        // occurrence/outbox, and make the missing transport presentation explicit without changing raw values.
        return input with { Data = data with { Presentation = null, ClientName = "[Client details omitted]" } };
    }

    private static string Cut(string text, int limit)
    {
        if (text.Length <= limit) return text;
        if (limit > 0 && char.IsHighSurrogate(text[limit - 1])) limit--;
        return text[..limit];
    }
}

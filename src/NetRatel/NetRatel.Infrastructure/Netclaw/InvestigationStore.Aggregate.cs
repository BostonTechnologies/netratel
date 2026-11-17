using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed record AggregateAlertPresence(DateTimeOffset ObservedAtUtc, MonitoringOfflineSnapshotDto? Current,
    IReadOnlyList<Guid> OfflineClientSample, bool SampleTruncated, int? NewlyOfflineCount, int? NoLongerOfflineCount);

public sealed partial class InvestigationStore
{
    private static readonly JsonSerializerOptions MonitoringJson = new(JsonSerializerDefaults.Web);

    private async Task<MonitoringOccurrenceDto> ValidateAggregateAlertAsync(int tenant, Guid occurrence, CancellationToken ct)
    {
        var json = await db.MonitoringOccurrences.AsNoTracking().Where(x => x.TenantId == tenant && x.OccurrenceId == occurrence &&
            x.AgentId == Guid.Empty && x.ResourceKey == MonitoringOfflinePresentation.ResourceKey)
            .Select(x => x.OccurrenceJson).SingleOrDefaultAsync(ct);
        return json is not null && JsonSerializer.Deserialize<MonitoringOccurrenceDto>(json, MonitoringJson) is { } alert
            ? alert : throw new KeyNotFoundException("The aggregate availability occurrence is unavailable in this tenant.");
    }

    private async Task<(MonitoringOccurrenceDto Alert, AggregateAlertPresence Presence)> ReadAggregateAlertAsync(
        int tenant, Guid occurrence, CancellationToken ct)
    {
        var alert = await ValidateAggregateAlertAsync(tenant, occurrence, ct);
        var (current, now) = await MonitoringStore.ObserveCurrentOfflineAsync(db, tenant, alert.PinnedRule.RuleId, clock, ct);
        var raisedJson = await db.MonitoringEvents.AsNoTracking().Where(x => x.TenantId == tenant && x.EventId == alert.RaisedEventId)
            .Select(x => x.EventJson).SingleOrDefaultAsync(ct);
        var raised = raisedJson is null ? null : JsonSerializer.Deserialize<MonitoringEventIntent>(raisedJson, MonitoringJson);
        var original = raised is { OfflineAgentIds.IsDefault: false } ? raised.OfflineAgentIds.ToHashSet() : null;
        const int maximumSample = 20;
        var ids = current?.AgentIds ?? [];
        return (alert, new(now, current?.Summary, ids.Take(maximumSample).ToArray(), ids.Length > maximumSample,
            original is null || current is null ? null : ids.Count(id => !original.Contains(id)),
            original is null || current is null ? null : original.Except(ids).Count()));
    }

    private async Task<PreparedInvestigationDto> PrepareAggregateAsync(int tenant, InvestigationCreateRequest request, CancellationToken ct)
    {
        var (alert, presence) = await ReadAggregateAlertAsync(tenant, request.OccurrenceId!.Value, ct);
        if (alert.PinnedRule.RuleId != request.PreparedAlert!.RuleId)
            throw new KeyNotFoundException("The original occurrence does not belong to the selected availability rule.");

        var original = alert.RaisedEvidence.OfflineSummary;
        static string Describe(MonitoringOfflineSnapshotDto? evidence) => evidence is null
            ? "unavailable" : $"{evidence.OfflineCount} offline of {evidence.MonitoredCount} monitored, " +
                $"{evidence.OfflineCount - evidence.SuppressedCount} actionable and {evidence.SuppressedCount} bypassed";
        var message = $"Diagnose tenant availability for occurrence {alert.OccurrenceId:D} (raised {alert.RaisedAtUtc:u}). " +
            $"Original evidence: {Describe(original)}. " +
            (alert.EndedAtUtc is { } ended ? $"The occurrence ended at {ended:u}. " : "The occurrence has no recorded end. ") +
            $"Presence checked at preparation ({presence.ObservedAtUtc:u}): {Describe(presence.Current)}. " +
            "Compare retained and current evidence, explain the scope and applicable bypasses, and identify supported next read-only checks.";
        return new(request.PreparedAlert, message, request.RequestId, presence.ObservedAtUtc)
        { Title = "Monitored clients · availability investigation" };
    }
}

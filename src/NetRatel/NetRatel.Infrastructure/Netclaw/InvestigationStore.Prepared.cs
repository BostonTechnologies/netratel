using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Netclaw;

public sealed partial class InvestigationStore
{
    // A prepared event is local context, never an operator message or a queued turn.
    // Keeping it in the existing durable journal also preserves request reconciliation
    // without a new table or changing historical investigation rows.
    private async Task<PreparedInvestigationDto?> ReadPreparedAsync(Guid id, CancellationToken ct)
    {
        var json = await db.Set<AiInvestigationEventRecord>().AsNoTracking()
            .Where(x => x.InvestigationId == id && x.Type == "prepared_alert").Select(x => x.Text).SingleOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<PreparedInvestigationDto>(json);
    }

    private async Task<PreparedInvestigationDto> PrepareAlertAsync(int tenant, InvestigationCreateRequest request, CancellationToken ct)
    {
        var reference = request.PreparedAlert!;
        if (reference.RuleId == Guid.Empty || string.IsNullOrWhiteSpace(reference.ResourceKey) || reference.ResourceKey.Length > 2048 ||
            request.AgentIds.Count != 1 || request.AgentIds[0] == Guid.Empty)
            throw new ArgumentException("Prepared alert context requires an exact rule, resource and single client.");
        var agent = request.AgentIds[0];
        var series = await db.Set<MonitoringSeriesRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant &&
            x.RuleId == reference.RuleId && x.AgentId == agent && x.ResourceKey == reference.ResourceKey, ct)
            ?? throw new KeyNotFoundException("The monitored resource does not belong to the selected tenant and client.");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var state = JsonSerializer.Deserialize<MonitoringSeriesState>(series.StateJson, options)
            ?? throw new KeyNotFoundException("Monitoring evidence is unavailable.");
        MonitoringOccurrenceDto? occurrence = null;
        if (request.OccurrenceId is { } occurrenceId)
        {
            var json = await db.MonitoringOccurrences.AsNoTracking().Where(x => x.TenantId == tenant && x.AgentId == agent &&
                x.RuleId == reference.RuleId && x.ResourceKey == reference.ResourceKey && x.OccurrenceId == occurrenceId)
                .Select(x => x.OccurrenceJson).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException("The alert context is unavailable.");
            occurrence = JsonSerializer.Deserialize<MonitoringOccurrenceDto>(json, options);
        }
        var definition = await db.Set<MonitoringRuleRecord>().AsNoTracking().Where(x => x.TenantId == tenant && x.RuleId == reference.RuleId)
            .Select(x => x.DefinitionJson).SingleOrDefaultAsync(ct);
        var rule = occurrence?.PinnedRule ?? (definition is null ? null : JsonSerializer.Deserialize<MonitoringRuleDto>(definition, options))
            ?? throw new KeyNotFoundException("The monitoring rule is unavailable.");
        var client = (await ClientsAsync(tenant, [agent], ct)).Single();
        var evidence = state.LatestEvidence;
        var now = clock.GetUtcNow();
        var fresh = evidence is not null && evidence.Quality == MonitoringEvidenceQuality.Fresh && evidence.ObservedAtUtc <= now &&
            evidence.ReceivedAtUtc <= now && now - evidence.ObservedAtUtc <= rule.FreshnessBudget && now - evidence.ReceivedAtUtc <= rule.FreshnessBudget;
        var reading = evidence?.NumericValue is { } value ? value.ToString("G6", CultureInfo.InvariantCulture) +
            (rule.Condition.Kind == MonitoringMetricKind.DiskFreePercent ? "% free" : rule.Condition.Kind == MonitoringMetricKind.CpuUsagePercent ? "% CPU" : " bytes free") : evidence?.ServiceState?.ToString() ?? "unknown";
        var message = $"Investigate {rule.Severity} monitoring on {client.HostName}. Rule: {rule.Name} ({rule.Condition.Kind}); breach {rule.Condition.BreachThreshold} {rule.Condition.Unit}, hold {rule.BreachHold.TotalSeconds:0} seconds. Resource: {reference.ResourceKey}. Latest reading: {reading}; {(fresh ? "fresh" : "stale or unknown")}; observed {evidence?.ObservedAtUtc.ToString("u") ?? "unknown"}; age {(evidence is null ? "unknown" : Math.Max(0, (now - evidence.ObservedAtUtc).TotalSeconds).ToString("0", CultureInfo.InvariantCulture) + " seconds")}. Alert raised {occurrence?.RaisedAtUtc.ToString("u") ?? "no occurrence associated"}. Context prepared {now:u} (UTC). Explain the likely cause and suggest next steps using authorized evidence.";
        if (message.Length > 6000) throw new ArgumentException("The prepared alert context exceeds the message budget.");
        var title = $"{rule.Name} · {client.HostName}";
        title = new string(title.Where(character => !char.IsControl(character)).Take(200).ToArray());
        return new(reference, message, request.RequestId, now) { Title = title };
    }
}

using System.Collections.Immutable;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Application.Monitoring;

/// <summary>One server presence observation of each eligible tenant client; missing or expired owners are offline.</summary>
public sealed record MonitoringOfflineClient(Guid AgentId, string Name, bool Offline);

public sealed record MonitoringOfflineObservation(MonitoringOfflineSnapshotDto Summary, ImmutableArray<Guid> AgentIds);

public static class MonitoringOfflineEvaluation
{
    public static MonitoringOfflineObservation Observe(MonitoringRuleDto rule, IReadOnlyList<MonitoringGroupDto> groups,
        IReadOnlyList<MonitoringOfflineClient> eligibleClients, IReadOnlyList<MonitoringBypassDto>? bypasses = null,
        DateTimeOffset now = default)
    {
        if (eligibleClients.Count > MonitoringLimits.MaximumTargetClients || eligibleClients.Any(client => client.AgentId == Guid.Empty))
            throw new ArgumentException("invalid_offline_directory");
        var targets = eligibleClients.Where(client => rule.Targets.Mode == MonitoringTargetMode.AllEligible ||
            rule.Targets.AgentIds.Contains(client.AgentId) || groups.Any(group => group.TenantId == rule.TenantId &&
                rule.Targets.GroupIds.Contains(group.GroupId) && group.AgentIds.Contains(client.AgentId))).ToArray();
        var offline = targets.Where(client => client.Offline).OrderBy(client => client.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(client => client.AgentId).ToArray();
        var series = MonitoringOfflinePresentation.Series(rule.TenantId, rule.RuleId);
        var actionable = bypasses is null ? offline : offline.Where(client => !MonitoringSeriesEvaluator.HasApplicableBypass(
            series with { AgentId = client.AgentId }, bypasses, now, groups)).ToArray();
        var names = string.Join(", ", actionable.Take(MonitoringOfflinePresentation.MaximumListedClients)
            .Select(client => CleanName(client.Name, client.AgentId)));
        var truncated = actionable.Length > MonitoringOfflinePresentation.MaximumListedClients || names.Length > MonitoringOfflinePresentation.MaximumClientNamesLength;
        return new(new(offline.Length, targets.Length, truncated ? "" : names, truncated, offline.Length - actionable.Length),
            offline.Select(client => client.AgentId).Order().ToImmutableArray());
    }

    public static MonitoringEvaluationResult Evaluate(MonitoringSeriesEvaluator evaluator, MonitoringSeriesState state,
        MonitoringRuleDto rule, MonitoringOfflineObservation observation, DateTimeOffset now,
        IReadOnlyList<MonitoringBypassDto> bypasses, IReadOnlyList<MonitoringGroupDto> groups)
    {
        var input = new MonitoringObservationDto(state.Series, new(1, checked((state.Cursor?.Sequence ?? 0) + 1)), rule.RuleId,
            now, now, true, true, observation.Summary.OfflineCount, OfflineSummary: observation.Summary);
        // Membership can change inside one outage. Only the evaluator creates a new occurrence after a completed recovery/clear window.
        return evaluator.Evaluate(state with { OfflineAgentIds = observation.AgentIds }, rule, input, 1, rule.RuleId, bypasses,
            targetApplicable: observation.Summary.MonitoredCount > 0, groups: groups);
    }

    private static string CleanName(string name, Guid id)
    {
        var clean = new string(name.Where(character => !char.IsControl(character)).Take(128).ToArray()).Trim();
        return clean.Length == 0 ? $"Client {id.ToString("N")[..8]}" : clean;
    }
}

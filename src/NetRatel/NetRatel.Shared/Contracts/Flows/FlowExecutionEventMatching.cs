using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetRatel.Shared.Contracts.Flows;

public sealed record FlowExecutionEventAggregateDto(Guid AdmissionKey, FlowExecutionEventDto Representative,
    IReadOnlyList<Guid> ContributingEventIds, bool Truncated = false);

/// <summary>
/// Pure content matching and identity only. The trusted ingress separately checks current authority, the exact
/// enabled/published interval, canonical source evidence and causation policy. Payloads never grant authority.
/// </summary>
public static class FlowExecutionEventMatching
{
    public static bool Matches(int tenantId, FlowExecutionTriggerSettingsDto settings, FlowExecutionEventDto value,
        DateTimeOffset activeSinceUtc)
    {
        if (settings is null || value is null || !SettingsValid(settings) ||
            !FlowExecutionEventValidator.Validate(value).Valid || tenantId != value.TenantId ||
            value.OccurredAtUtc < activeSinceUtc || value.ObservedAtUtc < activeSinceUtc ||
            !settings.Sources.Contains(value.SourceKind) || !settings.Outcomes.Contains(value.Outcome)) return false;
        if (settings.Scope == FlowExecutionScope.SelectedDefinitions)
        {
            var matches = settings.DefinitionReferences is { Count: > 0 } references
                ? references.Any(reference => reference.SourceKind == value.SourceKind &&
                    string.Equals(reference.Id, value.Definition.Id, StringComparison.Ordinal))
                : settings.Sources.Count == 1 && settings.DefinitionIds is { Count: > 0 } ids &&
                    ids.Contains(value.Definition.Id, StringComparer.Ordinal);
            if (!matches) return false;
        }
        if (settings.AgentIds is { Count: > 0 } agents &&
            (value.Target.AgentId is not { } agentId || !agents.Contains(agentId))) return false;
        if (value.SourceKind == FlowExecutionSourceKind.Request && settings.RequestKinds is { Count: > 0 } kinds &&
            !kinds.Contains(value.RequestKind, StringComparer.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// Every version/trigger is independent. Correlation is an accepted attempt key, never a root causation ID.
    /// Request-only failures use their own request key immediately; no nonexistent job is awaited.
    /// </summary>
    public static Guid AdmissionKey(Guid flowVersionId, Guid triggerNodeId, FlowExecutionTriggerSettingsDto settings,
        FlowExecutionEventDto value)
    {
        if (flowVersionId == Guid.Empty || triggerNodeId == Guid.Empty || !SettingsValid(settings) ||
            !FlowExecutionEventValidator.Validate(value).Valid)
            throw new ArgumentException("A valid immutable version, trigger and source event are required.");
        var aggregate = settings.AggregateCorrelatedEvents && settings.Sources.Count > 1 &&
            !string.IsNullOrWhiteSpace(value.CorrelationId);
        var parts = new[]
        {
            "flow-execution-admission/v1", flowVersionId.ToString("N"), triggerNodeId.ToString("N"),
            value.TenantId.ToString(CultureInfo.InvariantCulture),
            aggregate ? "correlated" : ((int)value.SourceKind).ToString(CultureInfo.InvariantCulture),
            aggregate ? value.CorrelationId! : value.ExecutionId,
            aggregate ? string.Empty : value.AttemptId,
            ((int)value.Outcome).ToString(CultureInfo.InvariantCulture)
        };
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))).AsSpan(0, 16));
    }

    /// <summary>
    /// Caller supplies same-key immutable facts already eligible for the subscription. A correlated request
    /// is linked only after the source writer proves the terminal job boundary, so that durable job fact can
    /// be selected regardless of delivery order. All source facts retain their own identities.
    /// </summary>
    public static FlowExecutionEventAggregateDto Aggregate(Guid flowVersionId, Guid triggerNodeId,
        FlowExecutionTriggerSettingsDto settings, IReadOnlyList<FlowExecutionEventDto> facts)
    {
        if (facts is null || facts.Count is 0 or > 256) throw new ArgumentException("A bounded nonempty fact set is required.");
        var key = AdmissionKey(flowVersionId, triggerNodeId, settings, facts[0]);
        if (facts.Any(fact => AdmissionKey(flowVersionId, triggerNodeId, settings, fact) != key))
            throw new ArgumentException("Unrelated source attempts or outcomes cannot be aggregated.");
        foreach (var duplicates in facts.GroupBy(fact => fact.EventId).Where(group => group.Count() > 1))
            if (duplicates.Select(fact => JsonSerializer.Serialize(fact, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto))
                .Distinct(StringComparer.Ordinal).Skip(1).Any())
                throw new ArgumentException("A source event identity cannot describe different immutable facts.");
        var ordered = facts.DistinctBy(fact => fact.EventId).OrderBy(fact => fact.SourceKind)
            .ThenBy(fact => fact.ExecutionId, StringComparer.Ordinal).ThenBy(fact => fact.AttemptId, StringComparer.Ordinal)
            .ThenBy(fact => fact.EventId).ToArray();
        return new(key, ordered[0], ordered.Take(FlowExecutionEventLimits.MaximumReferences).Select(fact => fact.EventId).ToArray(),
            ordered.Length > FlowExecutionEventLimits.MaximumReferences);
    }

    private static bool SettingsValid(FlowExecutionTriggerSettingsDto settings)
    {
        if (settings is null || settings.Sources is not { Count: > 0 and <= 3 } sources ||
            settings.Outcomes is not { Count: > 0 and <= 7 } outcomes || !Enum.IsDefined(settings.Scope) ||
            sources.Any(source => !Enum.IsDefined(source)) || sources.Distinct().Count() != sources.Count ||
            outcomes.Any(outcome => !Enum.IsDefined(outcome)) || outcomes.Distinct().Count() != outcomes.Count) return false;
        var ids = settings.DefinitionIds;
        var references = settings.DefinitionReferences;
        if (ids is { Count: > 0 } && (sources.Count != 1 || ids.Count > FlowGraphV2Limits.MaximumSelectedDefinitions ||
            ids.Any(id => !Identifier(id)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)) return false;
        if (references is { Count: > 0 } && (references.Count > FlowGraphV2Limits.MaximumSelectedDefinitions ||
            references.Any(reference => reference is null || !sources.Contains(reference.SourceKind) || !Identifier(reference.Id)) ||
            references.Distinct().Count() != references.Count)) return false;
        var hasIds = ids is { Count: > 0 };
        var hasReferences = references is { Count: > 0 };
        if (settings.Scope == FlowExecutionScope.AllInTenant && (hasIds || hasReferences) ||
            settings.Scope == FlowExecutionScope.SelectedDefinitions && hasIds == hasReferences) return false;
        if (settings.AgentIds is { Count: > 0 } agents && (agents.Count > FlowGraphV2Limits.MaximumSelectedDefinitions ||
            agents.Any(agent => agent == Guid.Empty) || agents.Distinct().Count() != agents.Count)) return false;
        if (settings.RequestKinds is { Count: > 0 } kinds && (!sources.Contains(FlowExecutionSourceKind.Request) ||
            kinds.Count > FlowGraphV2Limits.MaximumSelectedDefinitions || kinds.Any(kind => !Identifier(kind)) ||
            kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Count)) return false;
        return true;
    }

    private static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= FlowExecutionEventLimits.MaximumIdentifierLength && !value.Any(char.IsControl);
}

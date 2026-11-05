namespace NetRatel.Shared.Contracts.Flows;

/// <summary>Allowlisted v2 execution placeholders shared by authoring, validation and pure presentation.</summary>
public static class FlowExecutionMappingFields
{
    public static IReadOnlyList<string> AllowedNames { get; } = Array.AsReadOnly<string>(
    [
        "sourceKind", "definitionName", "definitionRevision", "executionId", "attemptId", "targetName",
        "outcome", "rawStatus", "reason", "exitCode", "startedAt", "completedAt", "occurredAt", "executionLink",
        "aiSummary", "aiEvidence", "aiRecommendations", "aiStatus", "aiObservedAt", "aiInvestigationId"
    ]);

    public static bool Contains(string name) => AllowedNames.Contains(name, StringComparer.Ordinal);
}

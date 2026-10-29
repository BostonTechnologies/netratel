namespace NetRatel.Application.Operations;

public sealed record McpPolicyConsolidationScope(int TenantId, string Name, int PolicyCount);
public sealed record McpPolicyConsolidationPlan(string PlanToken, DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<McpPolicyConsolidationScope> Scopes, IReadOnlyList<string> Conflicts, bool Applied = false);

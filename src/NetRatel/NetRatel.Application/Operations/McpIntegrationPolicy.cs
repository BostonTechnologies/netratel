using NetRatel.Shared.Operations;

namespace NetRatel.Application.Operations;

/// <summary>The explicitly reviewed policy for one tenant, or the separate server scope (zero).</summary>
public sealed record McpIntegrationPolicy(int TenantId, string Name, McpOperatorPolicyEffect Effect,
    McpOperatorOperationFamily Families, McpOperatorConstraints Constraints, DateTimeOffset ExpiresAtUtc)
{
    public IReadOnlyList<string>? Operations { get; init; }
    public Guid? ExistingPolicyId { get; init; }
    public long? ExpectedVersion { get; init; }
    public McpOperatorPolicyLifecycleState? LifecycleState { get; init; }
}

public static class McpIntegrationPolicyCatalog
{
    public static IReadOnlyList<McpOperatorOperationDescriptor> Supported(int tenantId, IEnumerable<string> permissions)
    {
        var grants = permissions.ToHashSet(StringComparer.Ordinal);
        return McpOperatorOperationCatalog.Operations.Where(operation =>
        {
            var model = McpOperationTargetCatalog.Find(operation.ToolName, operation.OperationName)?.Model;
            var required = McpLocalOperationAuthorizationCatalog.RequiredPermission(operation.ToolName, operation.OperationName);
            return model is not null && required is not null && grants.Contains(required) &&
                McpLocalOperationAuthorizationCatalog.AdditionalPermissions(operation.ToolName, operation.OperationName).All(grants.Contains) &&
                (tenantId == 0 ? model is McpOperationTargetModel.NoBusinessTarget or McpOperationTargetModel.OptionalTenant
                    : model != McpOperationTargetModel.NoBusinessTarget || operation.ToolName == "netratel_search" && operation.OperationName == "clients");
        }).ToArray();
    }

    // These are explicit, inspectable preset limits, never an implicit runtime capability grant.
    public static McpOperatorConstraints AllAccessLimits => new(
        ReadRoots: ["/", "C:\\"], WriteRoots: ["/", "C:\\"],
        AllowedShells: ["bash", "sh", "pwsh", "powershell", "cmd"], WorkingDirectories: ["/tmp", "C:\\Windows\\Temp"],
        MaxCommandDurationSeconds: 300, MaxTerminalIdleSeconds: 300, MaxTerminalLifetimeSeconds: 3600,
        MaxConcurrentTerminalSessions: 2, MaxConcurrentCommands: 4, MaxOutputBytes: 1_048_576,
        MaxArtifactBytes: 10_485_760, MaxScriptBytes: 1_048_576, MaxJobTargetCount: 100,
        MaxTaskTargetCount: 100, MaxFanOut: 100, MaxOnboardingCodeLifetimeSeconds: 3600,
        MaxOnboardingCodeUses: 10, DestructiveOperationsAllowed: true, AllowActiveTenantDeletion: false);

    public static IReadOnlyList<string> WithCanonicalPolicyOperations(IEnumerable<string> operations)
    {
        var selected = operations.ToHashSet(StringComparer.Ordinal);
        foreach (var (preview, confirm, canonical) in new[] {
            ("preview_create", "confirm_create", "create"), ("preview_replace", "confirm_replace", "replace"),
            ("preview_disable", "confirm_disable", "disable"), ("preview_revoke", "confirm_revoke", "revoke"),
            ("preview_target_profile", "confirm_target_profile", "upsert_target_profile") })
            if (selected.Contains("netratel_policy/" + preview) && selected.Contains("netratel_policy/" + confirm))
                selected.Add("netratel_policy/" + canonical);
        return selected.Order(StringComparer.Ordinal).ToArray();
    }

    public static McpOperatorPolicyDraft ToDraft(this McpOperatorPolicy policy) => new(policy.Name, policy.Environment,
        policy.Effect, policy.Priority, policy.PrincipalSelector, policy.TargetSelector, policy.TargetClassification,
        policy.OperationFamily, policy.Operation, policy.Constraints, policy.ExpiresAtUtc, policy.ReviewByUtc, policy.AuditReference)
    {
        ParentPolicyId = policy.ParentPolicyId, IntegrationCredentialId = policy.IntegrationCredentialId,
        Resource = policy.Resource, Operations = policy.Operations, IsContainer = policy.IsContainer
    };
}

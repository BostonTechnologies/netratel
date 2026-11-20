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
    public static void ValidateUsableLimits(McpIntegrationPolicy policy)
    {
        if (policy.Effect != McpOperatorPolicyEffect.Allow) return;
        var f = policy.Families; var c = policy.Constraints;
        if (c.Unrestricted) return;
        if (f.HasFlag(McpOperatorOperationFamily.FileRead) && c.ReadRoots is not { Count: > 0 } ||
            f.HasFlag(McpOperatorOperationFamily.FileWrite) && c.WriteRoots is not { Count: > 0 } ||
            (f.HasFlag(McpOperatorOperationFamily.TerminalExecute) || f.HasFlag(McpOperatorOperationFamily.TaskExecution)) &&
                (c.AllowedShells is not { Count: > 0 } || c.WorkingDirectories is not { Count: > 0 } || c.MaxCommandDurationSeconds is not > 0 || c.MaxOutputBytes is not > 0) ||
            f.HasFlag(McpOperatorOperationFamily.Onboarding) && (c.MaxOnboardingCodeLifetimeSeconds is not > 0 || c.MaxOnboardingCodeUses is not > 0))
            throw new ArgumentException("Selected operation families require explicit path, execution or onboarding limits.");
    }

    public static IReadOnlyList<McpOperatorOperationDescriptor> Supported(int tenantId, IEnumerable<string> permissions)
    {
        var grants = permissions.ToHashSet(StringComparer.Ordinal);
        return McpOperatorOperationCatalog.Operations.Where(operation =>
        {
            var model = McpOperationTargetCatalog.Find(operation.ToolName, operation.OperationName)?.Model;
            var required = McpLocalOperationAuthorizationCatalog.RequiredPermission(operation.ToolName, operation.OperationName);
            return model is not null && required is not null && grants.Contains(required) &&
                McpLocalOperationAuthorizationCatalog.AdditionalPermissions(operation.ToolName, operation.OperationName).All(grants.Contains) &&
                (tenantId == 0 ? model is McpOperationTargetModel.NoBusinessTarget or McpOperationTargetModel.OptionalTenant or McpOperationTargetModel.PolicySelector or McpOperationTargetModel.PolicyReference
                    : model != McpOperationTargetModel.NoBusinessTarget || operation.ToolName == "netratel_search" && operation.OperationName == "clients");
        }).ToArray();
    }

    public static McpOperatorConstraints AllAccessLimits => new() { Unrestricted = true, AllOperations = true };
    public const int MaximumPolicyNameLength = 160;

    public static string DefaultBaseName(string credentialName, string scopeName, string preset = "All access")
    {
        var prefix = $"{credentialName} · {scopeName}";
        var maximumPrefix = MaximumPolicyNameLength - preset.Length - " · ".Length;
        if (prefix.Length > maximumPrefix)
        {
            if (char.IsHighSurrogate(prefix[maximumPrefix - 1])) maximumPrefix--;
            prefix = prefix[..maximumPrefix].TrimEnd();
        }
        return $"{prefix} · {preset}";
    }

    public static McpOperatorOperationFamily FamiliesFor(IEnumerable<McpOperatorOperationDescriptor> operations) =>
        operations.Aggregate(McpOperatorOperationFamily.None, (families, operation) => families | operation.OperationFamily);

    public static IReadOnlyList<string> OperationsFor(IEnumerable<McpOperatorOperationDescriptor> operations, McpOperatorOperationFamily families) =>
        WithCanonicalPolicyOperations(operations.Where(operation => families.HasFlag(operation.OperationFamily))
            .Select(operation => operation.ToolName + "/" + operation.OperationName));

    /// <summary>Keep custom operations within the intersection of the previous and revised grants.</summary>
    public static McpIntegrationPolicy ReconcilePermissions(McpIntegrationPolicy policy, IEnumerable<string> previousPermissions,
        IEnumerable<string> permissions)
    {
        var unchanged = permissions.ToHashSet(StringComparer.Ordinal).SetEquals(previousPermissions);
        var supported = Supported(policy.TenantId, permissions);
        var families = FamiliesFor(supported);
        if (policy.Constraints.AllOperations)
        {
            var constraints = policy.Constraints;
            if (constraints.FullPermissionCatalog && !unchanged)
                constraints = constraints with { FullPermissionCatalog = false, PermissionCatalogSnapshot = null };
            return policy with { Families = families, Operations = null, Constraints = constraints };
        }

        var retainedFamilies = policy.Families & families;
        var allowed = OperationsFor(supported, retainedFamilies).ToHashSet(StringComparer.Ordinal);
        if (unchanged && policy.Operations is null)
            return policy with { Families = retainedFamilies };
        var previous = policy.Operations ?? OperationsFor(Supported(policy.TenantId, previousPermissions), policy.Families);
        var retained = previous.Where(allowed.Contains).ToArray();
        if (unchanged) return policy with { Families = retainedFamilies, Operations = retained };
        var coveredFamilies = supported.Select(operation => operation.OperationFamily).Distinct()
            .Where(family => OperationsFor(supported, family).Any(retained.Contains))
            .Aggregate(McpOperatorOperationFamily.None, (current, family) => current | family);
        return policy with { Families = coveredFamilies, Operations = retained };
    }

    // Retained for custom bounded policies and exact recognition of the original preset on upgrade.
    public static McpOperatorConstraints BoundedDefaults => new(
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

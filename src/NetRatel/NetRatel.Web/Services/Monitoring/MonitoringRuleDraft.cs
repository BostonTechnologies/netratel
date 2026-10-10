using System.Collections.Immutable;
using System.Text.Json;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Web.Services.Monitoring;

/// <summary>A detached editor draft. Validation never writes or attributes an execution principal.</summary>
public sealed class MonitoringRuleDraft
{
    public Guid RuleId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public MonitoringSeverity Severity { get; set; } = MonitoringSeverity.Warning;
    public MonitoringTargetMode TargetMode { get; set; } = MonitoringTargetMode.Selected;
    public HashSet<Guid> AgentIds { get; set; } = [];
    public HashSet<Guid> GroupIds { get; set; } = [];
    public MonitoringMetricKind Metric { get; set; } = MonitoringMetricKind.CpuUsagePercent;
    public MonitoringNumericUnit Unit { get; set; } = MonitoringNumericUnit.Percent;
    public double BreachThreshold { get; set; } = 90;
    public double RecoveryThreshold { get; set; } = 80;
    public string ResourceName { get; set; } = "";
    public ClientServicePlatform Platform { get; set; } = ClientServicePlatform.Windows;
    public HashSet<ClientServiceState> ExpectedStates { get; set; } = [ClientServiceState.Running];
    public int BreachHoldSeconds { get; set; } = 60;
    public int RecoveryHoldSeconds { get; set; } = 30;
    public int FreshnessSeconds { get; set; } = 90;
    public Guid? PublishedFlowVersionId { get; set; }
    public string Reason { get; set; } = "";
    public bool ResetConfirmed { get; set; }
    public MonitoringRuleDto? Original { get; private set; }
    private string _initial = "";

    public bool Dirty => _initial != Signature();
    public static MonitoringRuleDraft Create(MonitoringRuleDto? rule = null)
    {
        var draft = new MonitoringRuleDraft { Original = rule };
        if (rule is not null)
        {
            draft.RuleId = rule.RuleId; draft.Name = rule.Name; draft.Enabled = rule.Enabled; draft.Severity = rule.Severity;
            draft.TargetMode = rule.Targets.Mode; draft.AgentIds = rule.Targets.AgentIds.ToHashSet(); draft.GroupIds = rule.Targets.GroupIds.ToHashSet();
            draft.Metric = rule.Condition.Kind; draft.Unit = rule.Condition.Unit ?? MonitoringNumericUnit.Percent;
            draft.BreachThreshold = rule.Condition.BreachThreshold ?? 0; draft.RecoveryThreshold = rule.Condition.RecoveryThreshold ?? 0;
            draft.ResourceName = rule.Condition.ResourceName ?? ""; draft.Platform = rule.Condition.ServicePlatform ?? ClientServicePlatform.Windows;
            draft.ExpectedStates = rule.Condition.ExpectedServiceStates.IsDefault ? [] : rule.Condition.ExpectedServiceStates.ToHashSet();
            draft.BreachHoldSeconds = (int)rule.BreachHold.TotalSeconds; draft.RecoveryHoldSeconds = (int)rule.RecoveryHold.TotalSeconds;
            draft.FreshnessSeconds = (int)rule.FreshnessBudget.TotalSeconds; draft.PublishedFlowVersionId = rule.PublishedFlowVersionId;
        }
        draft._initial = draft.Signature();
        return draft;
    }

    public void AdoptReviewedRevision(MonitoringRuleDto current)
    {
        if (Original is null || current.RuleId != RuleId || current.TenantId != Original.TenantId)
            throw new ArgumentException("A matching existing rule is required.", nameof(current));
        Original = current;
        ResetConfirmed = false;
    }

    public void SelectMetric(MonitoringMetricKind metric)
    {
        Metric = metric;
        if (metric == MonitoringMetricKind.CpuUsagePercent) { Unit = MonitoringNumericUnit.Percent; BreachThreshold = 90; RecoveryThreshold = 80; ResourceName = ""; }
        else if (metric == MonitoringMetricKind.DiskFreePercent) { Unit = MonitoringNumericUnit.Percent; BreachThreshold = 10; RecoveryThreshold = 15; ResourceName = ""; }
        else if (metric == MonitoringMetricKind.DiskFreeSpace) { Unit = MonitoringNumericUnit.GiB; BreachThreshold = 5; RecoveryThreshold = 8; ResourceName = ""; }
        else { ResourceName = ""; ExpectedStates = [ClientServiceState.Running]; }
    }

    public MonitoringRuleDto Build(int tenantId)
    {
        var targets = new MonitoringTargetSelectionDto(TargetMode,
            TargetMode == MonitoringTargetMode.AllEligible ? [] : AgentIds.Order().ToImmutableArray(),
            TargetMode == MonitoringTargetMode.AllEligible ? [] : GroupIds.Order().ToImmutableArray());
        var condition = Metric == MonitoringMetricKind.ServiceExpectedState
            ? new MonitoringConditionDto(Metric, null, null, null, ResourceName.Trim(), Platform, ExpectedStates.Order().ToImmutableArray())
            : new MonitoringConditionDto(Metric, Unit, BreachThreshold, RecoveryThreshold,
                Metric == MonitoringMetricKind.CpuUsagePercent || string.IsNullOrWhiteSpace(ResourceName) ? null : ResourceName.Trim(), null, []);
        var candidate = new MonitoringRuleDto(tenantId, RuleId, (Original?.Revision ?? 0) + 1, Original?.EvaluationRevision ?? 1,
            Name.Trim(), Enabled, Severity, targets, condition, TimeSpan.FromSeconds(BreachHoldSeconds), TimeSpan.FromSeconds(RecoveryHoldSeconds),
            TimeSpan.FromSeconds(FreshnessSeconds), PublishedFlowVersionId,
            PublishedFlowVersionId == Original?.PublishedFlowVersionId ? Original?.ExecutionPrincipalId : null,
            PublishedFlowVersionId == Original?.PublishedFlowVersionId ? Original?.ExecutionCredentialId : null);
        // The evaluator revision changes only through an explicitly acknowledged condition/scope reset.
        if (Original is not null && ValidForDefinition(candidate) &&
            MonitoringContractValidator.EvaluationFingerprint(candidate) != MonitoringContractValidator.EvaluationFingerprint(Original))
            candidate = candidate with { EvaluationRevision = Original.EvaluationRevision + 1 };
        return candidate;
    }

    public string? Validate(int tenantId, MonitoringPermissionsDto permissions, IReadOnlyList<MonitoringPublishedFlowDto> flows)
        => Validate(tenantId, permissions, flows, out _);

    public string? Validate(int tenantId, MonitoringPermissionsDto permissions, IReadOnlyList<MonitoringPublishedFlowDto> flows, out string? field)
    {
        field = null;
        if (!permissions.CanManage || permissions.TenantId != tenantId) return "You do not have permission to manage these rules.";
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > MonitoringLimits.MaximumNameLength)
            return Invalid(nameof(Name), "Enter a rule name (up to 128 characters).", out field);
        var rule = Build(tenantId);
        if (TargetMode == MonitoringTargetMode.AllEligible && !permissions.CanTargetAll)
            return Invalid(nameof(TargetMode), "All eligible clients requires tenant-wide target permission.", out field);
        if (!MonitoringContractValidator.TryValidateTargets(rule.Targets, out _))
            return Invalid(nameof(TargetMode), "Select at least one client or group within the target limits.", out field);
        if (BreachHoldSeconds is < 1 or > 3600) return Invalid(nameof(BreachHoldSeconds), "Full breach hold must be 1–3600 seconds.", out field);
        if (RecoveryHoldSeconds is < 1 or > 3600) return Invalid(nameof(RecoveryHoldSeconds), "Full recovery hold must be 1–3600 seconds.", out field);
        if (FreshnessSeconds is < 1 or > 900) return Invalid(nameof(FreshnessSeconds), "Freshness budget must be 1–900 seconds.", out field);
        if (!ValidForDefinition(rule))
        {
            if (Metric == MonitoringMetricKind.ServiceExpectedState)
                return Invalid(ExpectedStates.Count == 0 ? nameof(ExpectedStates) : nameof(ResourceName), "Enter an exact stable service name and select its expected states for this platform.", out field);
            return Invalid(nameof(BreachThreshold), "Check the exact volume, units and thresholds. CPU breach must exceed recovery; disk breach must be below recovery.", out field);
        }
        if (PublishedFlowVersionId is { } flow && !flows.Any(item => item.PublishedFlowVersionId == flow))
            return Invalid(nameof(PublishedFlowVersionId), "Select a currently published flow version or Display only.", out field);
        if (Original is not null && rule.EvaluationRevision != Original.EvaluationRevision && !ResetConfirmed)
            return Invalid(nameof(ResetConfirmed), "Confirm the condition/scope reset. Any active occurrence is suspended and a new full window is required.", out field);
        if (string.IsNullOrWhiteSpace(Reason) || Reason.Length > MonitoringLimits.MaximumReasonLength)
            return Invalid(nameof(Reason), "Enter a change reason (up to 512 characters).", out field);
        return null;
    }

    private static string Invalid(string name, string message, out string? field) { field = name; return message; }

    private static bool ValidForDefinition(MonitoringRuleDto rule) => MonitoringContractValidator.TryValidateRule(
        rule with { PublishedFlowVersionId = null, ExecutionPrincipalId = null, ExecutionCredentialId = null }, out _);
    private string Signature() => JsonSerializer.Serialize(new { Name, Enabled, Severity, TargetMode, Agents = AgentIds.Order(), Groups = GroupIds.Order(), Metric, Unit,
        BreachThreshold, RecoveryThreshold, ResourceName, Platform, States = ExpectedStates.Order(), BreachHoldSeconds, RecoveryHoldSeconds, FreshnessSeconds, PublishedFlowVersionId });
}

using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Shared.Contracts.FileSystem;
using System.Security.Cryptography;
using System.Text;

namespace NetRatel.Application.Monitoring;

public static class MonitoringContractValidator
{
    public static bool TryValidateRule(MonitoringRuleDto rule, out string? error)
    {
        error = null;
        if (rule.TenantId <= 0 || rule.RuleId == Guid.Empty || rule.Revision == 0 || rule.EvaluationRevision == 0 ||
            !Text(rule.Name, MonitoringLimits.MaximumNameLength) || !Enum.IsDefined(rule.Severity) ||
            rule.BreachHold < MonitoringLimits.MinimumHold || rule.BreachHold > MonitoringLimits.MaximumHold ||
            rule.RecoveryHold < MonitoringLimits.MinimumHold || rule.RecoveryHold > MonitoringLimits.MaximumHold ||
            rule.FreshnessBudget < MonitoringLimits.MinimumFreshness || rule.FreshnessBudget > MonitoringLimits.MaximumFreshness ||
            rule.PublishedFlowVersionId == Guid.Empty)
            return Fail("invalid_rule", out error);
        if (rule.PublishedFlowVersionId is not null
            ? !Text(rule.ExecutionPrincipalId, MonitoringLimits.MaximumExecutionIdentityLength) ||
                (rule.ExecutionCredentialId is not null && !Text(rule.ExecutionCredentialId, MonitoringLimits.MaximumExecutionIdentityLength))
            : rule.ExecutionPrincipalId is not null || rule.ExecutionCredentialId is not null)
            return Fail("invalid_execution_authority", out error);
        if (!TryValidateTargets(rule.Targets, out error)) return false;
        var condition = rule.Condition;
        if (!Enum.IsDefined(condition.Kind)) return Fail("invalid_condition", out error);
        if (condition.Kind == MonitoringMetricKind.ServiceExpectedState)
        {
            if (condition.Unit is not null || condition.BreachThreshold is not null || condition.RecoveryThreshold is not null ||
                condition.ServicePlatform is not (ClientServicePlatform.Windows or ClientServicePlatform.LinuxSystemd) ||
                !ClientServiceContractValidator.IsValidServiceName(condition.ResourceName, condition.ServicePlatform.Value) ||
                condition.ExpectedServiceStates.IsDefaultOrEmpty || condition.ExpectedServiceStates.Length > 6 ||
                condition.ExpectedServiceStates.Distinct().Count() != condition.ExpectedServiceStates.Length ||
                condition.ExpectedServiceStates.Any(state => state is not (ClientServiceState.Running or ClientServiceState.Stopped or ClientServiceState.Failed or
                    ClientServiceState.Starting or ClientServiceState.Stopping or ClientServiceState.Paused)))
                return Fail("invalid_service_condition", out error);
            return true;
        }
        if (condition.ServicePlatform is not null || !condition.ExpectedServiceStates.IsDefaultOrEmpty ||
            condition.Unit is null || !Enum.IsDefined(condition.Unit.Value) ||
            condition.BreachThreshold is not double breach || condition.RecoveryThreshold is not double recovery ||
            !double.IsFinite(breach) || !double.IsFinite(recovery)) return Fail("invalid_numeric_condition", out error);
        if (condition.Kind is MonitoringMetricKind.CpuUsagePercent or MonitoringMetricKind.DiskFreePercent)
        {
            if (condition.Unit != MonitoringNumericUnit.Percent || breach < 0 || breach > 100 || recovery < 0 || recovery > 100)
                return Fail("invalid_percent_condition", out error);
        }
        else if (condition.Unit == MonitoringNumericUnit.Percent || breach < 0 || recovery < 0 ||
            ToCanonicalBytes(breach, condition.Unit.Value) > Math.Pow(1024, 6) || ToCanonicalBytes(recovery, condition.Unit.Value) > Math.Pow(1024, 6))
            return Fail("invalid_space_condition", out error);
        if (condition.Kind == MonitoringMetricKind.CpuUsagePercent)
        {
            if (condition.ResourceName is not null || breach <= recovery) return Fail("invalid_cpu_hysteresis", out error);
        }
        else if (breach >= recovery || (condition.ResourceName is not null &&
            (!Text(condition.ResourceName, 256) || !RemoteFilePath.TryNormalize(condition.ResourceName, out var normalized) || normalized != condition.ResourceName)))
            return Fail("invalid_disk_hysteresis", out error);
        return true;
    }

    public static bool TryValidateTargets(MonitoringTargetSelectionDto targets, out string? error)
    {
        error = null;
        if (!Enum.IsDefined(targets.Mode) || targets.AgentIds.IsDefault || targets.GroupIds.IsDefault ||
            targets.AgentIds.Length > MonitoringLimits.MaximumTargetClients || targets.GroupIds.Length > MonitoringLimits.MaximumGroupsPerTenant ||
            targets.AgentIds.Any(id => id == Guid.Empty) || targets.GroupIds.Any(id => id == Guid.Empty) ||
            targets.AgentIds.Distinct().Count() != targets.AgentIds.Length || targets.GroupIds.Distinct().Count() != targets.GroupIds.Length ||
            (targets.Mode == MonitoringTargetMode.AllEligible && (targets.AgentIds.Length != 0 || targets.GroupIds.Length != 0)) ||
            (targets.Mode == MonitoringTargetMode.Selected && targets.AgentIds.Length + targets.GroupIds.Length == 0))
            return Fail("invalid_targets", out error);
        return true;
    }

    public static bool TryValidateGroup(MonitoringGroupDto group, out string? error)
    {
        error = null;
        if (group.TenantId <= 0 || group.GroupId == Guid.Empty || group.Revision == 0 ||
            !Text(group.Name, MonitoringLimits.MaximumNameLength) || group.AgentIds.IsDefaultOrEmpty ||
            group.AgentIds.Length > MonitoringLimits.MaximumTargetClients || group.AgentIds.Any(id => id == Guid.Empty) ||
            group.AgentIds.Distinct().Count() != group.AgentIds.Length)
            return Fail("invalid_group", out error);
        return true;
    }

    public static bool TryValidateBypass(MonitoringBypassDto bypass, out string? error)
    {
        error = null;
        if (bypass.BypassId == Guid.Empty || bypass.TenantId <= 0 || bypass.OperatorId == Guid.Empty ||
            bypass.RuleId == Guid.Empty || bypass.AgentId == Guid.Empty || bypass.GroupId == Guid.Empty || !Text(bypass.Reason, MonitoringLimits.MaximumReasonLength) ||
            (bypass.ResourceKey is not null && !Text(bypass.ResourceKey, MonitoringLimits.MaximumResourceKeyLength)) ||
            bypass.StartsAtUtc == default || (bypass.ExpiresAtUtc is { } expiry && expiry <= bypass.StartsAtUtc))
            return Fail("invalid_bypass", out error);
        return true;
    }

    public static void RequireRule(MonitoringRuleDto rule)
    {
        if (!TryValidateRule(rule, out var error)) throw new ArgumentException(error, nameof(rule));
    }

    public static bool TryValidateIncidentReceipt(MonitoringIncidentReceiptDto receipt, out string? error)
    {
        error = null;
        if (!Text(receipt.IncidentId, 256) || (receipt.TrackingNumber is not null && !Text(receipt.TrackingNumber, 256)) ||
            (receipt.IncidentUrl is not null && (!Text(receipt.IncidentUrl, 2048) ||
                !Uri.TryCreate(receipt.IncidentUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo)))) return Fail("invalid_incident_receipt", out error);
        return true;
    }

    public static void RequireReason(Guid operatorId, string reason)
    {
        if (operatorId == Guid.Empty || !Text(reason, MonitoringLimits.MaximumReasonLength)) throw new ArgumentException("operator_and_reason_required");
    }

    public static double ToCanonicalBytes(double value, MonitoringNumericUnit unit) => value * (unit switch
    {
        MonitoringNumericUnit.Bytes => 1d,
        MonitoringNumericUnit.KiB => 1024d,
        MonitoringNumericUnit.MiB => 1024d * 1024,
        MonitoringNumericUnit.GiB => 1024d * 1024 * 1024,
        _ => throw new ArgumentException("absolute_space_unit_required", nameof(unit))
    });

    public static string EvaluationFingerprint(MonitoringRuleDto rule)
    {
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("netratel.monitoring.condition.v1");
            writer.Write((int)rule.Targets.Mode);
            foreach (var id in rule.Targets.AgentIds.Order()) writer.Write(id.ToByteArray());
            writer.Write(Guid.Empty.ToByteArray());
            foreach (var id in rule.Targets.GroupIds.Order()) writer.Write(id.ToByteArray());
            writer.Write(Guid.Empty.ToByteArray());
            var condition = rule.Condition;
            writer.Write((int)condition.Kind);
            writer.Write(condition.Kind == MonitoringMetricKind.DiskFreeSpace ? (int)MonitoringNumericUnit.Bytes : condition.Unit is { } unit ? (int)unit : -1);
            writer.Write(condition.Kind == MonitoringMetricKind.DiskFreeSpace ? ToCanonicalBytes(condition.BreachThreshold!.Value, condition.Unit!.Value) : condition.BreachThreshold ?? double.NaN);
            writer.Write(condition.Kind == MonitoringMetricKind.DiskFreeSpace ? ToCanonicalBytes(condition.RecoveryThreshold!.Value, condition.Unit!.Value) : condition.RecoveryThreshold ?? double.NaN);
            writer.Write(condition.Kind == MonitoringMetricKind.ServiceExpectedState && condition.ServicePlatform == ClientServicePlatform.Windows
                ? condition.ResourceName!.ToUpperInvariant() : condition.ResourceName ?? string.Empty);
            writer.Write(condition.ServicePlatform is { } platform ? (int)platform : -1);
            if (!condition.ExpectedServiceStates.IsDefault)
                foreach (var state in condition.ExpectedServiceStates.Order()) writer.Write((int)state);
            writer.Write(-1);
            writer.Write(rule.BreachHold.Ticks);
            writer.Write(rule.RecoveryHold.Ticks);
            writer.Write(rule.FreshnessBudget.Ticks);
        }
        return Convert.ToHexString(SHA256.HashData(memory.ToArray()));
    }

    private static bool Text(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Any(char.IsControl)) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
    private static bool Fail(string value, out string? error) { error = value; return false; }
}

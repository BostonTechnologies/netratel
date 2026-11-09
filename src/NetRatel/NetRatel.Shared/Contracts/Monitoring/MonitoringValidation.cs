namespace NetRatel.Shared.Contracts.Monitoring;

/// <summary>Only catalogued codes and paths cross the API/tool trust boundary.</summary>
public sealed record MonitoringValidationIssue(string Code, string Field, string Remediation);

public static class MonitoringValidationErrors
{
    public static MonitoringValidationIssue? ForCode(string? code, bool preview = false)
    {
        var condition = preview ? "data.condition" : "data.rule.condition";
        return code switch
        {
            "monitoring_cpu_resource_requires_null" => new(code, condition + ".resourceName", "CPU requires explicit JSON null for resourceName; do not use an empty string or a CPU name."),
            "monitoring_disk_resource_invalid" => new(code, condition + ".resourceName", "Use explicit JSON null for all eligible disks, or an exact normalized path such as /data or C:\\. Empty strings and * are invalid."),
            "monitoring_numeric_service_platform" => new(code, condition + ".servicePlatform", "Numeric conditions require explicit JSON null for servicePlatform."),
            "monitoring_numeric_service_states" => new(code, condition + ".expectedServiceStates", "Numeric conditions require expectedServiceStates: []."),
            "monitoring_cpu_hysteresis" => new(code, condition + ".recoveryThreshold", "CPU uses Percent from 0 to 100, with breachThreshold greater than recoveryThreshold."),
            "monitoring_disk_hysteresis" => new(code, condition + ".recoveryThreshold", "Disk free thresholds require breachThreshold less than recoveryThreshold. 80%/90% used means 20%/10% free."),
            "invalid_numeric_condition" => new(code, condition, "Supply a numeric unit and finite breachThreshold/recoveryThreshold, servicePlatform:null and expectedServiceStates:[]."),
            "invalid_percent_condition" => new(code, condition + ".unit", "Use Percent and finite thresholds between 0 and 100. DiskFreePercent measures free space."),
            "invalid_space_condition" => new(code, condition, "Use Bytes, KiB, MiB or GiB and nonnegative finite thresholds up to 1024^6 canonical bytes."),
            "invalid_service_condition" => new(code, condition, "Use an exact supported service name, Windows or LinuxSystemd, explicit null numeric fields and distinct supported expected service states."),
            "invalid_targets" => new(code, preview ? "data.targets" : "data.rule.targets", "Use Selected with nonempty unique authorized agentIds/groupIds, or AllEligible with both arrays empty and the all-target permission."),
            "invalid_rule" => new(code, "data.rule", "Use positive tenant/revisions, a nonempty UUID and name, valid severity, holds and freshness within the published limits."),
            "invalid_execution_authority" => new(code, "data.rule.publishedFlowVersionId", "Use an authorized published flow or null. Execution principal and credential are derived by the server."),
            "invalid_condition" => new(code, condition + ".kind", "Choose CpuUsagePercent, DiskFreePercent, DiskFreeSpace or ServiceExpectedState."),
            _ => null
        };
    }

    public static MonitoringValidationIssue? FromWire(string? code, string? field)
    {
        // Never trust upstream remediation or an arbitrary field string.
        foreach (var preview in new[] { false, true })
            if (ForCode(code, preview) is { } issue && issue.Field == field) return issue;
        return null;
    }
}

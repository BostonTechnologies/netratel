using System.Globalization;
using System.Text.Json;
using System.Text;
using NetRatel.Shared.Client;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Application.Monitoring;

/// <summary>Rendered once at the occurrence transaction, never from the live directory during delivery or retry.</summary>
public static class MonitoringIncidentPresentation
{
    public static MonitoringPresentationSnapshotDto Create(MonitoringSeriesKey series, MonitoringOccurrenceDto occurrence,
        string? tenantName, MonitoringClientIdentityDto? identity, string? publicWebBase)
    {
        var rule = occurrence.PinnedRule;
        var evidence = occurrence.RaisedEvidence;
        var offline = evidence.OfflineSummary;
        var isOffline = MonitoringOfflinePresentation.IsSeries(series);
        var name = isOffline ? "Monitored clients" : Compact(Clean(identity?.DisplayName, 160, "Client details unavailable"), 256);
        var host = isOffline || string.IsNullOrWhiteSpace(identity?.Hostname) ? null : Compact(Clean(identity.Hostname, 160, ""), 256);
        if (identity?.Deleted is true) name = Compact(Clean(name + " (deleted)", 200, "Deleted client"), 256);
        var tenant = Compact(Clean(tenantName, 96, "Tenant details unavailable"), 192);
        var resource = Clean(series.ResourceKey, MonitoringLimits.MaximumResourceKeyLength, "Resource unavailable");
        var mount = Compact(resource.StartsWith("disk:", StringComparison.Ordinal) ? resource[5..] : resource, 384);
        var facts = evidence.DiskFacts is { TotalBytes: > 0 } disk && disk.FreeBytes <= disk.TotalBytes ? disk : null;
        var percent = rule.Condition.Kind == MonitoringMetricKind.DiskFreePercent ? evidence.NumericValue :
            facts is null ? (double?)null : 100d * facts.FreeBytes / facts.TotalBytes;
        var formatted = isOffline ? offline?.Summary ?? "Client availability unavailable" : rule.Condition.Kind switch
        {
            MonitoringMetricKind.DiskFreePercent or MonitoringMetricKind.DiskFreeSpace when percent is { } free =>
                Number(free) + "% free (" + Number(100 - free) + "% used)",
            MonitoringMetricKind.DiskFreeSpace when evidence.NumericValue is { } bytes => Bytes(bytes) + " free",
            MonitoringMetricKind.ServiceExpectedState => evidence.ServiceState?.ToString() ?? "Service evidence unavailable",
            _ when evidence.NumericValue is { } number => Number(number) + "%",
            _ => "Measured value unavailable"
        };
        var condition = isOffline ? "Offline for " + Seconds(rule.OfflineGraceSeconds ?? MonitoringOfflinePresentation.DefaultGraceSeconds) :
            Condition(rule, recovery: false);
        var recovery = isOffline ? "Client availability restored" : Condition(rule, recovery: true);
        var title = isOffline ? $"{rule.Severity} · Client availability · {offline?.OfflineCount - offline?.SuppressedCount ?? 0} offline" :
            rule.Condition.Kind is MonitoringMetricKind.DiskFreePercent or MonitoringMetricKind.DiskFreeSpace
                ? $"{rule.Severity} · {host ?? name} · {mount} · {formatted}"
                : $"{rule.Severity} · {host ?? name} · {rule.Name} · {formatted}";
        string? clientUrl = null, alertUrl = null;
        var unavailableLinkReason = "configure the public Web base";
        try
        {
            if (!string.IsNullOrWhiteSpace(publicWebBase))
            {
                var web = ClientEndpointAddress.NormalizePublicWebBase(publicWebBase);
                var alert = $"{web}/monitoring?tenantId={series.TenantId.ToString(CultureInfo.InvariantCulture)}&alert={occurrence.OccurrenceId:D}";
                var client = $"{web}/clients?tenantId={series.TenantId.ToString(CultureInfo.InvariantCulture)}&clientId={series.AgentId:D}";
                if (alert.Length <= MonitoringPresentationSnapshot.MaximumLinkLength) alertUrl = alert;
                else unavailableLinkReason = "the public Web link exceeds the presentation limit";
                if (!isOffline && client.Length <= MonitoringPresentationSnapshot.MaximumLinkLength) clientUrl = client;
            }
        }
        catch (ArgumentException) { unavailableLinkReason = "the configured public Web base is invalid"; }
        var lines = new List<string> { "Tenant: " + tenant, isOffline ? "Client availability" : "Client: " + name };
        if (host is not null && host != name) lines.Add("Hostname: " + host);
        if (isOffline) lines.Add(offline?.Summary ?? "Affected clients unavailable");
        else
        {
            lines.Add("Resource: " + mount);
            if (facts?.Metadata is { } metadata)
            {
                if (!string.IsNullOrWhiteSpace(metadata.FileSystem)) lines.Add("Filesystem: " + Clean(metadata.FileSystem, 64, ""));
                if (!string.IsNullOrWhiteSpace(metadata.Source)) lines.Add("Device: " + Clean(metadata.Source, 128, ""));
            }
            if (facts is not null) lines.Add("Capacity: " + Bytes(facts.FreeBytes) + " free / " + Bytes(facts.TotalBytes) + " total");
            lines.Add("Measured: " + formatted);
        }
        lines.Add("Breach: " + condition); lines.Add("Recovery: " + recovery);
        lines.Add("Severity: " + rule.Severity + " · State: Firing");
        lines.Add("Observed: " + evidence.ObservedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        lines.Add("Raised: " + occurrence.RaisedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        if (clientUrl is not null) lines.Add("Open client: " + clientUrl);
        if (alertUrl is not null) lines.Add("Open alert: " + alertUrl);
        else lines.Add("Links unavailable: " + unavailableLinkReason + ".");
        var snapshot = new MonitoringPresentationSnapshotDto(MonitoringPresentationSnapshot.CurrentVersion, series.TenantId,
            series.RuleId, series.AgentId, occurrence.OccurrenceId, tenant, name, host,
            Compact(Clean(title, 200, "Monitoring alert"), 768), string.Join('\n', lines), Compact(Clean(formatted, 256, "Unavailable"), 384),
            Compact(condition, 384), Compact(recovery, 384),
            evidence.ObservedAtUtc, occurrence.RaisedAtUtc, clientUrl, alertUrl,
            facts is null ? null : new(facts.TotalBytes, facts.FreeBytes));
        // Bound the complete escaped JSON, not only its character count. Keep exact counters and correlation fields.
        while (JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > MonitoringPresentationSnapshot.MaximumBytes && snapshot.Details.Length > 64)
            snapshot = snapshot with { Details = Cut(snapshot.Details, snapshot.Details.Length - 64) + "\n[Details truncated; see alert.]" };
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > MonitoringPresentationSnapshot.MaximumBytes)
            snapshot = snapshot with { ClientUrl = null, AlertUrl = null, Details = "Presentation truncated. Links unavailable because the bounded presentation budget was exceeded." };
        if (!MonitoringPresentationSnapshot.IsValid(snapshot)) throw new ArgumentException("invalid_monitoring_presentation");
        return snapshot;
    }

    private static string Condition(MonitoringRuleDto rule, bool recovery)
    {
        var condition = rule.Condition;
        if (condition.Kind == MonitoringMetricKind.ServiceExpectedState)
            return (recovery ? "Expected service state: " : "Service outside expected state: ") +
                string.Join(", ", condition.ExpectedServiceStates) + " for " + Seconds((recovery ? rule.RecoveryHold : rule.BreachHold).TotalSeconds);
        var threshold = recovery ? condition.RecoveryThreshold : condition.BreachThreshold;
        if (threshold is null) return "Threshold unavailable";
        var value = condition.Kind == MonitoringMetricKind.DiskFreeSpace ? Number(threshold.Value) + " " + condition.Unit : Number(threshold.Value) + "%";
        var direction = condition.Kind == MonitoringMetricKind.CpuUsagePercent ? (recovery ? "below " : "above ") : (recovery ? "above " : "below ");
        return (condition.Kind == MonitoringMetricKind.CpuUsagePercent ? "CPU " : "Free space ") + direction + value + " for " +
            Seconds((recovery ? rule.RecoveryHold : rule.BreachHold).TotalSeconds);
    }

    private static string Seconds(double value) => Number(value) + " seconds";
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    public static string Bytes(double value) => value >= 1024d * 1024 * 1024 ? Number(value / (1024d * 1024 * 1024)) + " GiB" :
        value >= 1024 * 1024 ? Number(value / (1024 * 1024)) + " MiB" : value >= 1024 ? Number(value / 1024) + " KiB" : Number(value) + " bytes";
    private static string Clean(string? value, int limit, string fallback)
    {
        var text = new StringBuilder();
        foreach (var rune in (string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()).EnumerateRunes())
            if (!Rune.IsControl(rune)) text.Append(rune.ToString());
        return Cut(text.ToString(), limit);
    }
    private static string Compact(string text, int maximumBytes)
    {
        while (JsonSerializer.SerializeToUtf8Bytes(text).Length > maximumBytes) text = Cut(text, text.Length - 1);
        return text;
    }
    private static string Cut(string text, int limit)
    {
        if (text.Length <= limit) return text;
        var end = limit;
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end];
    }
}

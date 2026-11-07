namespace NetRatel.Shared.Contracts.Monitoring;

public enum MonitoringView { All, Active, Firing, Pending, Unknown, Acknowledged, Suppressed, Offline }
public enum MonitoringSort { SeriesIdentity, EventTimeDescending }

/// <summary>AND-combined selection over the full authorized result, before ordering and paging.
/// Series dates are occurrence RaisedAtUtc; history dates are immutable event/audit AtUtc.
/// ToExclusiveUtc is exclusive; callers convert inclusive operator-local end dates before sending.</summary>
public sealed record MonitoringQueryDto(int TenantId, MonitoringView View = MonitoringView.All,
    Guid? ClientId = null, MonitoringSeverity? Severity = null, string? Text = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? ToExclusiveUtc = null,
    MonitoringSort? Sort = null, int MaximumCount = 50)
{
    public MonitoringQueryDto Normalize(bool history = false)
    {
        var expectedSort = history ? MonitoringSort.EventTimeDescending : MonitoringSort.SeriesIdentity;
        if (TenantId <= 0 || !Enum.IsDefined(View) || ClientId == Guid.Empty ||
            Severity is { } severity && !Enum.IsDefined(severity) ||
            Sort is { } sort && sort != expectedSort || MaximumCount is < 1 or > MonitoringLimits.MaximumRowsPerRead ||
            Text is { Length: > MonitoringQueryLimits.MaximumTextLength } || Text?.Any(char.IsControl) == true ||
            FromUtc is { } from && ToExclusiveUtc is { } to && from >= to)
            throw new ArgumentException("invalid_monitoring_query");
        if (history && View != MonitoringView.All) throw new ArgumentException("invalid_monitoring_history_view");
        return this with { Text = string.IsNullOrWhiteSpace(Text) ? null : Text.Trim().ToLowerInvariant(),
            FromUtc = FromUtc?.ToUniversalTime(), ToExclusiveUtc = ToExclusiveUtc?.ToUniversalTime(), Sort = expectedSort };
    }
}

public static class MonitoringQueryLimits
{
    public const int MaximumTextLength = 256;
    // Binary v1: magic(4), version(1), kind(1), tenant(4), normalized-query SHA256(32),
    // rule(16), agent(16), resource UTF16 length(2), all 512 UTF16 code units(1024).
    // Raw UTF16 preserves escaped and Unicode keys exactly, with no JSON escaping expansion.
    public const int MaximumCursorBytes = 4 + 1 + 1 + 4 + 32 + 16 + 16 + 2 + MonitoringLimits.MaximumResourceKeyLength * 2;
    public const int MaximumCursorLength = ((MaximumCursorBytes + 2) / 3) * 4;
}

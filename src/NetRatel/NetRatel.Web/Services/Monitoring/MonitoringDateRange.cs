using System.Globalization;

namespace NetRatel.Web.Services.Monitoring;

public static class MonitoringDateRange
{
    /// <summary>Operator-local calendar dates, with an inclusive end converted to the next local midnight.</summary>
    public static (DateTimeOffset? FromUtc, DateTimeOffset? ToExclusiveUtc) ToUtc(string? from, string? to, TimeZoneInfo zone)
    {
        static DateOnly? Parse(string? value) => string.IsNullOrEmpty(value) ? null :
            DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date : throw new ArgumentException("Enter valid From and To dates.");
        var start = Parse(from); var end = Parse(to);
        if (start > end) throw new ArgumentException("From must be on or before To.");
        DateTimeOffset Convert(DateOnly date)
        {
            var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            // A few zones advance at midnight. The selected day starts at its first valid local instant.
            for (var minutes = 0; zone.IsInvalidTime(local) && minutes < 24 * 60; minutes++) local = local.AddMinutes(1);
            if (zone.IsInvalidTime(local)) throw new ArgumentException("The selected date is unavailable in this timezone.");
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
        }
        if (end == DateOnly.MaxValue) throw new ArgumentException("Choose a To date before 9999-12-31.");
        return (start is { } first ? Convert(first) : null, end is { } last ? Convert(last.AddDays(1)) : null);
    }
}

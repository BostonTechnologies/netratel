using System.Globalization;

namespace NetRatel.Web.Services;

/// <summary>Exact UTC instants; a duration preset never rounds to a calendar boundary.</summary>
public static class IntegrationCredentialExpiry
{
    public static DateTimeOffset ThirtyDaysFrom(DateTimeOffset reference) => reference.ToUniversalTime().Add(TimeSpan.FromHours(30 * 24));
    public static string Format(DateTimeOffset instant) => instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    public static DateTimeOffset? ParseUtcInstant(string? value) => value is not null && value.EndsWith('Z') &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant : null;
    // Retained only for compatibility with historical callers. Creation and approval use ParseUtcInstant.
    public static DateTimeOffset? EndOfUtcDay(DateTime? selectedDate) => selectedDate is null ? null :
        new DateTimeOffset(DateTime.SpecifyKind(selectedDate.Value.Date, DateTimeKind.Utc).AddDays(1).AddTicks(-1));
}

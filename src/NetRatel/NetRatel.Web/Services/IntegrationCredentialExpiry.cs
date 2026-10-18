using NetRatel.Shared.Connectivity;

namespace NetRatel.Web.Services;

/// <summary>Exact UTC instants; a duration preset never rounds to a calendar boundary.</summary>
public static class IntegrationCredentialExpiry
{
    // The generated preset starts at the same representable instant shown to the reviewer.
    public static DateTimeOffset Reference(DateTimeOffset clockReading) => new(clockReading.UtcTicks - clockReading.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
    public static DateTimeOffset ThirtyDaysFrom(DateTimeOffset reference)
    {
        reference = reference.ToUniversalTime();
        if (!ReviewedMcpAccessExpiry.IsSupported(reference)) throw new ArgumentOutOfRangeException(nameof(reference));
        return reference.Add(TimeSpan.FromHours(30 * 24));
    }
    public static string Format(DateTimeOffset instant) => ReviewedMcpAccessExpiry.Format(instant);
    public static string Display(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return ReviewedMcpAccessExpiry.IsSupported(utc) ? Format(utc)
            : utc.UtcDateTime.ToString("O") + " (precision requires review; not supported in a reviewed proposal)";
    }
    public static DateTimeOffset? ParseUtcInstant(string? value) => ReviewedMcpAccessExpiry.TryParse(value, out var instant) ? instant : null;
    // Retained only for compatibility with historical callers. Creation and approval use ParseUtcInstant.
    public static DateTimeOffset? EndOfUtcDay(DateTime? selectedDate) => selectedDate is null ? null :
        new DateTimeOffset(DateTime.SpecifyKind(selectedDate.Value.Date, DateTimeKind.Utc).AddDays(1).AddTicks(-1));
}

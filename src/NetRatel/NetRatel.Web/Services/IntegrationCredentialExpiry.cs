namespace NetRatel.Web.Services;

/// <summary>Interprets a selected calendar date as the final instant of that UTC day.</summary>
public static class IntegrationCredentialExpiry
{
    public static DateTimeOffset? EndOfUtcDay(DateTime? selectedDate)
    {
        if (selectedDate is null) return null;
        var utcDay = DateTime.SpecifyKind(selectedDate.Value.Date, DateTimeKind.Utc);
        return new DateTimeOffset(utcDay.AddDays(1).AddTicks(-1));
    }
}

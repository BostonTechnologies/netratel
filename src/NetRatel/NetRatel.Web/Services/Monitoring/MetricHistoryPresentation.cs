using System.Globalization;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Web.Services.Monitoring;

public static class MetricHistoryPresentation
{
    public static string Time(DateTimeOffset? at, TimeZoneInfo zone) => at is { } date
        ? TimeZoneInfo.ConvertTime(date, zone).ToString("dd MMM HH:mm:ss zzz", CultureInfo.CurrentCulture) : "—";

    public static string Value(double? value, string unit) => value is not { } number ? "Unavailable" :
        unit == "%" ? number.ToString("0.#", CultureInfo.CurrentCulture) + "%" : FormatBytes(number, unit == "B/s");

    public static string Bytes(string? value) => decimal.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
        ? FormatBytes((double)bytes, false) : "Unavailable";

    public static string Duration(long microseconds)
    {
        var seconds = microseconds / 1_000_000d;
        if (seconds >= 86_400) return $"{seconds / 86_400:0.#} days";
        if (seconds >= 3600) return $"{seconds / 3600:0.#} h";
        if (seconds >= 60) return $"{seconds / 60:0.#} min";
        return $"{seconds:0.#} s";
    }

    public static string Quality(MetricHistoryQuality quality)
    {
        if (quality == MetricHistoryQuality.None) return "Observed";
        var labels = new List<string>();
        foreach (var (flag, label) in QualityLabels)
            if ((quality & flag) != 0) labels.Add(label);
        return string.Join(" · ", labels);
    }

    private static string FormatBytes(double bytes, bool rate)
    {
        var unit = 0;
        var value = bytes;
        while (Math.Abs(value) >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {ByteUnits[unit]}{(rate ? "/s" : "")}";
    }

    private static readonly string[] ByteUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
    private static readonly (MetricHistoryQuality Flag, string Name)[] QualityLabels =
    [
        (MetricHistoryQuality.Legacy, "Legacy sample only"),
        (MetricHistoryQuality.LegacyWindowsCpu, "Windows CPU requires an updated client"),
        (MetricHistoryQuality.WarmUp, "Warming up"),
        (MetricHistoryQuality.Reset, "Counter reset"),
        (MetricHistoryQuality.Missing, "Missing"),
        (MetricHistoryQuality.Failed, "Collection failed"),
        (MetricHistoryQuality.Unsupported, "Unsupported"),
        (MetricHistoryQuality.InventoryPartial, "Inventory incomplete"),
        (MetricHistoryQuality.EstimatedTiming, "Estimated timing"),
        (MetricHistoryQuality.PartialEdge, "Clipped retained interval"),
        (MetricHistoryQuality.PartialCoverage, "Partial coverage"),
        (MetricHistoryQuality.UnclassifiedDisk, "Unclassified volume")
    ];
}

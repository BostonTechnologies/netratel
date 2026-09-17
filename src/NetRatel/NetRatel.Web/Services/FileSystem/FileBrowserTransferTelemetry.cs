using System.Diagnostics.Metrics;
using System.Diagnostics;

namespace NetRatel.Web.Services.FileSystem;

internal static class FileBrowserTransferTelemetry
{
    private static readonly Meter Meter = new("NetRatel.FileBrowser", "1.0.0");
    private static readonly Counter<long> Transfers = Meter.CreateCounter<long>("file_browser_transfers_total");
    private static readonly Counter<long> Bytes = Meter.CreateCounter<long>("file_browser_transfer_bytes_total", "By");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("file_browser_transfer_duration_seconds", "s");

    public static void Record(string operation, string outcome, long bytes, TimeSpan duration)
    {
        var tags = new TagList { { "operation", operation }, { "outcome", outcome } };
        Transfers.Add(1, tags);
        Bytes.Add(Math.Max(0, bytes), tags);
        Duration.Record(duration.TotalSeconds, tags);
    }
}

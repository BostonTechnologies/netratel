using System.Text;
using System.Text.Json;

namespace NetRatel.Shared.Utils;

public static class ApiAnsiAdapter
{
    // Requires AnsiConsole in the same namespace (NetRatel.Shared.Utils)
    public static (string Plain, string Html, int ExitCode) Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("stdout", out var stdout) || stdout.ValueKind != JsonValueKind.Array)
            {
                // Not the expected exec result; just echo JSON
                var safe = AnsiConsole.StripAnsi(json);
                var html = $"<pre>{System.Net.WebUtility.HtmlEncode(safe)}</pre>";
                return (safe, html, 0);
            }

            var sbPlain = new StringBuilder();
            var sbAnsi = new StringBuilder();

            foreach (var line in stdout.EnumerateArray())
            {
                var s = line.GetString() ?? string.Empty;
                sbPlain.AppendLine(AnsiConsole.StripAnsi(s));
                sbAnsi.AppendLine(s);
            }

            var exit = root.TryGetProperty("exitCode", out var ecEl) && ecEl.TryGetInt32(out var ec) ? ec : 0;
            var htmlOut = AnsiConsole.AnsiToHtml(sbAnsi.ToString());

            return (sbPlain.ToString(), htmlOut, exit);
        }
        catch
        {
            var safe = AnsiConsole.StripAnsi(json);
            var html = $"<pre>{System.Net.WebUtility.HtmlEncode(safe)}</pre>";
            return (safe, html, 0);
        }
    }
}

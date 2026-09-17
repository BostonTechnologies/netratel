using System.Text;
using System.Text.Json;
using NetRatel.Shared.Utils;

namespace NetRatel.Web.Services;

/// <summary>
/// Shared helper to parse execution return payloads into plain/html output.
/// </summary>
public static class ExecOutputFormatter
{
    public static (string Plain, string Html, int ExitCode) ParseExecReturn(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // If it's not the expected shape, just return as-is
            if (!root.TryGetProperty("stdout", out var stdout) || stdout.ValueKind != JsonValueKind.Array)
                return (json, $"<pre>{System.Net.WebUtility.HtmlEncode(json)}</pre>", 0);

            var sbPlain = new StringBuilder();
            var sbHtml = new StringBuilder();

            foreach (var line in stdout.EnumerateArray())
            {
                var s = line.GetString() ?? string.Empty;
                var cleaned = AnsiConsole.NormalizeForDisplay(s);
                sbPlain.AppendLine(cleaned);
                sbHtml.AppendLine(System.Net.WebUtility.HtmlEncode(cleaned));
            }

            if (root.TryGetProperty("stderr", out var stderr) && stderr.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in stderr.EnumerateArray())
                {
                    var s = line.GetString() ?? string.Empty;
                    var cleaned = AnsiConsole.NormalizeForDisplay(s);
                    if (string.IsNullOrWhiteSpace(cleaned))
                    {
                        continue;
                    }

                    sbPlain.AppendLine(cleaned);
                    sbHtml.Append("<span style=\"color:#ff6b6b\">")
                        .Append(System.Net.WebUtility.HtmlEncode(cleaned))
                        .AppendLine("</span>");
                }
            }

            var exitCode = root.TryGetProperty("exitCode", out var ecEl) && ecEl.TryGetInt32(out var ec) ? ec : 0;
            var html = $"<pre style=\"margin:0\">{sbHtml}</pre>";

            return (sbPlain.ToString(), html, exitCode);
        }
        catch
        {
            // Fallback if parsing fails
            var plain = AnsiConsole.NormalizeForDisplay(json);
            var html = $"<pre>{System.Net.WebUtility.HtmlEncode(plain)}</pre>";
            return (plain, html, 0);
        }
    }
}

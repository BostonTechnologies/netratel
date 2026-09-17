using System.Text;
using System.Text.RegularExpressions;

namespace NetRatel.Shared.Utils;

public static class AnsiConsole
{
    // Very small subset: reset/bold/fg colors used by PowerShell + your client logs
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;]*m", RegexOptions.Compiled);

    public static string StripAnsi(string input) => AnsiRegex.Replace(input ?? string.Empty, string.Empty);

    public static string NormalizeForDisplay(string input)
    {
        var cleaned = StripAnsi(input ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        cleaned = Regex.Replace(cleaned, @"(?<=:\d)Line \|", "\nLine |");
        cleaned = Regex.Replace(cleaned, @"\s+\|\s+", "\n| ");
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");

        return cleaned.Trim();
    }

    public static string AnsiToHtml(string input)
    {
        var s = input ?? string.Empty;

        // Map a few common tokens to spans. Do simple replaces; order matters.
        s = s.Replace("\u001b[0m", "</span>");           // reset
        s = s.Replace("\u001b[1m", "<span style=\"font-weight:600\">");

        // Foreground colors frequently seen in your payloads
        s = s.Replace("\u001b[31;1m", "<span style=\"color:#ff6b6b;font-weight:600\">"); // bright red
        s = s.Replace("\u001b[31m", "<span style=\"color:#ff6b6b\">");
        s = s.Replace("\u001b[32;1m", "<span style=\"color:#3ad29f;font-weight:600\">"); // bright green
        s = s.Replace("\u001b[32m", "<span style=\"color:#3ad29f\">");
        s = s.Replace("\u001b[36;1m", "<span style=\"color:#7dd3fc;font-weight:600\">"); // cyan
        s = s.Replace("\u001b[36m", "<span style=\"color:#7dd3fc\">");
        s = s.Replace("\u001b[44;1m", "<span style=\"background:#1f3b65;color:#e6e6e6;padding:0 2px;border-radius:3px\">"); // blue bg “block”

        // Strip any remaining control codes to avoid stray escapes
        s = NormalizeForDisplay(s);

        // Wrap as pre to preserve layout; our own <span> closes above will be balanced enough for our usage
        return $"<pre style=\"margin:0\">{s}</pre>";
    }
}

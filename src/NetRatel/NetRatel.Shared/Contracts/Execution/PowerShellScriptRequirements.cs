using System.Text.RegularExpressions;

namespace NetRatel.Shared.Contracts.Execution;

public static class PowerShellScriptRequirements
{
    public static (bool Desktop, bool Core) Read(string script)
    {
        // Only choose an engine for the supported #requires engine/version hints.
        // The selected external engine still parses and validates the original
        // script, including all other #requires options and exact versions.
        var requiresDesktop = false;
        var requiresCore = false;
        foreach (var options in ReadPowerShellRequirements(script))
        {
            var edition = PowerShellEditionPattern.Match(options).Groups["edition"].Value;
            requiresDesktop |= edition.Equals("Desktop", StringComparison.OrdinalIgnoreCase);
            requiresCore |= edition.Equals("Core", StringComparison.OrdinalIgnoreCase);
            var versionText = PowerShellVersionPattern.Match(options).Groups["version"].Value;
            if (Version.TryParse(versionText.Contains('.') ? versionText : versionText + ".0", out var version) &&
                (version.Major > 5 || version.Major == 5 && version.Minor > 1)) requiresCore = true;
        }
        return (requiresDesktop, requiresCore);
    }

    private static IEnumerable<string> ReadPowerShellRequirements(string script)
    {
        using var reader = new StringReader(script);
        var blockCommentDepth = 0;
        char? quote = null;
        string? hereStringEnd = null;
        while (reader.ReadLine() is { } line)
        {
            var offset = 0;
            if (hereStringEnd is not null)
            {
                if (!line.StartsWith(hereStringEnd, StringComparison.Ordinal)) continue;
                offset = hereStringEnd.Length;
                hereStringEnd = null;
            }
            for (var index = offset; index < line.Length; index++)
            {
                var character = line[index];
                var next = index + 1 < line.Length ? line[index + 1] : '\0';
                if (blockCommentDepth > 0)
                {
                    if (character == '<' && next == '#') { blockCommentDepth++; index++; }
                    else if (character == '#' && next == '>') { blockCommentDepth--; index++; }
                    continue;
                }
                if (quote is not null)
                {
                    if (quote == '"' && character == '`') { index++; continue; }
                    if (character != quote) continue;
                    if (quote == '\'' && next == '\'') { index++; continue; }
                    quote = null;
                    continue;
                }
                if (character == '`') { index++; continue; }
                if (character == '<' && next == '#') { blockCommentDepth++; index++; continue; }
                if (character == '#')
                {
                    const string directive = "#requires";
                    var comment = line[index..];
                    if (comment.StartsWith(directive, StringComparison.OrdinalIgnoreCase) &&
                        comment.Length > directive.Length && char.IsWhiteSpace(comment[directive.Length]))
                        yield return comment[directive.Length..].Trim();
                    break;
                }
                if (character == '@' && next is '\'' or '"' && string.IsNullOrWhiteSpace(line[(index + 2)..]))
                {
                    hereStringEnd = next + "@";
                    break;
                }
                if (character is '\'' or '"') quote = character;
            }
        }
    }

    private static readonly Regex PowerShellEditionPattern = new(
        "(?:^|\\s)-PSEdition\\s+['\"]?(?<edition>Desktop|Core)['\"]?(?=\\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PowerShellVersionPattern = new(
        @"(?:^|\s)-Version\s+(?<version>\d+(?:\.\d+){0,3})(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

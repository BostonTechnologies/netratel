using System.Text.RegularExpressions;

namespace NetRatel.Shared.Contracts;

public static class TenantUpdatePolicyValidation
{
    private static readonly Regex SemanticVersion = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string? TargetVersionError(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        return SemanticVersion.IsMatch(version)
            ? null
            : "Target version must be a semantic version, for example 1.2.3 or 1.2.3-beta.1.";
    }

    public static bool IsTargetInChannel(string? channel, string? version) =>
        string.IsNullOrWhiteSpace(version) ||
        string.Equals(channel, "prerelease", StringComparison.OrdinalIgnoreCase) ||
        !version.Split('+')[0].Contains('-');
}

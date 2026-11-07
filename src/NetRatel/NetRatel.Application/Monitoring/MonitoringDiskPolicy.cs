using System.Collections.Immutable;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Application.Monitoring;

/// <summary>One policy interpretation shared by evaluation, saved rules, and cached inventory previews.</summary>
public static class MonitoringDiskPolicy
{
    public const int MaximumPatterns = 32;
    public const int MaximumPatternLength = 256;

    public static bool TryValidate(MonitoringDiskPolicyDto? policy, out string? error)
    {
        error = null;
        if (policy is null) return true;
        var patterns = All(policy).ToArray();
        if (patterns.Length > MaximumPatterns || patterns.Any(pattern => string.IsNullOrWhiteSpace(pattern) ||
            pattern.Length > MaximumPatternLength || pattern.Any(char.IsControl) || !ValidUnicode(pattern)))
        { error = "invalid_disk_policy"; return false; }
        return true;
    }

    public static MonitoringDiskPolicyDto? Normalize(MonitoringDiskPolicyDto? policy)
    {
        if (!TryValidate(policy, out var error)) throw new ArgumentException(error);
        if (policy is null || !All(policy).Any()) return null;
        return new(Sort(policy.IncludeMounts), Sort(policy.ExcludeMounts), Sort(policy.IncludeFileSystems), Sort(policy.ExcludeFileSystems));
    }

    public static (bool Eligible, string Reason) Select(string mount, TelemetryDiskMetadataDto? metadata, MonitoringConditionDto condition)
    {
        var comparison = IsWindows(mount) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (condition.ResourceName is { } exact && !string.Equals(exact, mount, comparison)) return (false, "exact-resource-mismatch");
        var policy = Normalize(condition.DiskPolicy);
        var fileSystem = metadata?.FileSystem;
        if (policy is not null)
        {
            if (Matches(policy.ExcludeMounts, mount, comparison) || fileSystem is not null &&
                Matches(policy.ExcludeFileSystems, fileSystem, StringComparison.OrdinalIgnoreCase)) return (false, "explicit-exclude");
            var hasIncludes = !policy.IncludeMounts.IsDefaultOrEmpty || !policy.IncludeFileSystems.IsDefaultOrEmpty;
            var included = Matches(policy.IncludeMounts, mount, comparison) || fileSystem is not null &&
                Matches(policy.IncludeFileSystems, fileSystem, StringComparison.OrdinalIgnoreCase);
            if (hasIncludes && !included) return (false, "outside-explicit-includes");
            if (included) return (true, metadata is null ? "included-unclassified" : "explicit-include");
        }
        return metadata?.DefaultExclusionReason is { } reason ? (false, reason) :
            (true, metadata is null ? "unclassified-legacy" : "eligible");
    }

    public static bool Equivalent(MonitoringDiskPolicyDto? left, MonitoringDiskPolicyDto? right)
    {
        left = Normalize(left); right = Normalize(right);
        if (left is null || right is null) return left is null && right is null;
        return left.IncludeMounts.SequenceEqual(right.IncludeMounts) && left.ExcludeMounts.SequenceEqual(right.ExcludeMounts) &&
            left.IncludeFileSystems.SequenceEqual(right.IncludeFileSystems) && left.ExcludeFileSystems.SequenceEqual(right.ExcludeFileSystems);
    }

    public static bool IsValidMetadata(TelemetryDiskMetadataDto? metadata) => metadata is null ||
        Text(metadata.FileSystem, 32) && Text(metadata.Source, 256) && Text(metadata.MountRoot, 256) &&
        metadata.CapacityKey is { Length: 64 } && metadata.CapacityKey.All(char.IsAsciiHexDigit) &&
        metadata.DefaultExclusionReason is null or "package-image" or "pseudo-filesystem" or "duplicate-capacity";

    private static bool Text(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit &&
        !value.Any(char.IsControl) && ValidUnicode(value);
    private static bool ValidUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
    private static IEnumerable<string> All(MonitoringDiskPolicyDto policy) => Values(policy.IncludeMounts).Concat(Values(policy.ExcludeMounts))
        .Concat(Values(policy.IncludeFileSystems)).Concat(Values(policy.ExcludeFileSystems));
    private static IEnumerable<string> Values(ImmutableArray<string> patterns) => patterns.IsDefault ? [] : patterns;
    private static ImmutableArray<string> Sort(ImmutableArray<string> patterns) => Values(patterns).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
    private static bool IsWindows(string path) => path.Length >= 2 && path[1] == ':' || path.StartsWith("\\\\", StringComparison.Ordinal);
    private static bool Matches(ImmutableArray<string> patterns, string value, StringComparison comparison) =>
        !patterns.IsDefaultOrEmpty && patterns.Any(pattern => Glob(pattern, value, comparison));

    // Bounded linear-space wildcard matching; no regular expressions or platform-dependent path expansion.
    private static bool Glob(string pattern, string value, StringComparison comparison)
    {
        var p = 0; var v = 0; var star = -1; var retry = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || string.Equals(pattern.Substring(p, 1), value.Substring(v, 1), comparison))) { p++; v++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; retry = v; }
            else if (star >= 0) { p = star + 1; v = ++retry; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

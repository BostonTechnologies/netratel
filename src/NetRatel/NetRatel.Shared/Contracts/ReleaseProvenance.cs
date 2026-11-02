using System.Globalization;
using System.Reflection;

namespace NetRatel.Shared.Contracts;

public sealed record ReleaseProvenance(string? Commit, DateTimeOffset? BuiltAtUtc)
{
    public static ReleaseProvenance Read(Assembly? assembly, string? informationalVersion = null)
    {
        var metadata = assembly?.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray() ?? [];
        var revision = metadata.FirstOrDefault(x => x.Key == "SourceRevisionId")?.Value;
        informationalVersion ??= assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        revision ??= informationalVersion?.Split('+').ElementAtOrDefault(1);
        var timestamp = metadata.FirstOrDefault(x => x.Key == "BuildTimestampUtc")?.Value;
        return new(ValidCommit(revision), ParseTimestamp(timestamp));
    }

    public static string? ValidCommit(string? value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit)
        ? value.ToLowerInvariant() : null;

    public static DateTimeOffset? ParseTimestamp(string? value) => DateTimeOffset.TryParseExact(value,
        ["yyyy-MM-dd'T'HH:mm:ss'Z'", "O"], CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;

    public string Display => $"{(ValidCommit(Commit) is { } sha ? $"commit {sha[..7]}" : "commit unavailable")} · {(BuiltAtUtc is { } built ? $"built {built.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "build date unavailable")}";
}

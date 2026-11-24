using System.Globalization;
using NetRatel.Application.Secrets;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Infrastructure.Secrets;

internal static class SecretProviderValidation
{
    public const int MaximumSecretLength = 128 * 1024;
    public const int MaximumBrowseLimit = 100;

    public static SecretProviderException Invalid(string message) => new(SecretProviderFailure.InvalidRequest, message);
    public static SecretProviderException Missing() => new(SecretProviderFailure.NotFound, "The requested vault object was not found.");
    public static SecretProviderException MissingVersion() => new(SecretProviderFailure.VersionUnavailable, "The selected secret version is missing or unavailable.");
    public static SecretProviderException Conflict() => new(SecretProviderFailure.Conflict, "The object changed. Refresh its metadata before trying again.");
    public static SecretProviderException InUse() => new(SecretProviderFailure.InUse, "The object has saved consumers. Review its usage before removing it.");
    public static SecretProviderException Unavailable() => new(SecretProviderFailure.Unavailable, "The provider could not complete the request.");

    public static string RequiredText(string? text, int limit, string field)
    {
        var normalized = text?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > limit || normalized.Any(char.IsControl))
            throw Invalid($"{field} must contain 1 to {limit} characters without control characters.");
        return normalized;
    }

    public static string? OptionalText(string? text, int limit, string field) =>
        string.IsNullOrWhiteSpace(text) ? null : RequiredText(text, limit, field);

    public static long PositiveInt64(string? value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 ||
            number.ToString(CultureInfo.InvariantCulture) != value)
            throw Invalid("Supply a valid positive decimal version or revision.");
        return number;
    }

    public static string Path(string? path, bool allowRoot)
    {
        if (allowRoot && string.IsNullOrEmpty(path))
            return string.Empty;
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Contains('\\') || path.Contains('%') ||
            path.Any(char.IsControl) || path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Trim() != part))
            throw Invalid("The locator or folder contains an invalid path segment.");
        return path;
    }

    public static string Segment(string? value)
    {
        var segment = Path(value, allowRoot: false);
        if (segment.Contains('/'))
            throw Invalid("Supply one path segment.");
        return segment;
    }

    public static string JoinPath(string first, string second) =>
        first.Length == 0 ? second : second.Length == 0 ? first : $"{first}/{second}";

    public static string EncodePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public static void RequireExpected(SecretEntryMetadata metadata, string? revision, string? version)
    {
        if (string.IsNullOrEmpty(revision) || string.IsNullOrEmpty(version))
            throw Invalid("Supply the current secret revision and version.");
        if (!StringComparer.Ordinal.Equals(metadata.Revision, revision) || !StringComparer.Ordinal.Equals(metadata.Version, version))
            throw Conflict();
    }

    public static void CheckScalarWrite(WriteSecretEntryRequest request)
    {
        if (request.Fields is not null || request.RemoveFields is not null)
            throw Invalid("This provider stores scalar secret values.");
        if (request.Value?.Length > MaximumSecretLength)
            throw Invalid("The secret value exceeds the size limit.");
    }
}

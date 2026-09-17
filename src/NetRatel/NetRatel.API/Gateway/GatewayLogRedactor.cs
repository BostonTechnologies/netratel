using System.Text.RegularExpressions;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Gateway;

/// <summary>
/// Removes known credential forms before log records enter any browser or
/// operator-facing gateway response. This is a defense-in-depth boundary;
/// clients must still avoid emitting secrets in their log payloads.
/// </summary>
public static partial class GatewayLogRedactor
{
    private const string Redacted = "[REDACTED]";

    public static GatewayLogRecordDto Redact(GatewayLogRecordDto record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record with
        {
            Message = RedactText(record.Message),
            StructuredProperties = RedactProperties(record.StructuredProperties)
        };
    }

    private static IReadOnlyDictionary<string, string>? RedactProperties(IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null || properties.Count == 0)
            return null;

        var redacted = new Dictionary<string, string>(properties.Count, StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            redacted[key] = IsSensitivePropertyName(key)
                ? Redacted
                : RedactText(value);
        }

        return redacted;
    }

    private static bool IsSensitivePropertyName(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("api-key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("apikey", StringComparison.OrdinalIgnoreCase);

    private static string RedactText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var redacted = BearerRegex().Replace(value, "$1[REDACTED]");
        redacted = CookieRegex().Replace(redacted, "$1[REDACTED]");
        redacted = SecretRegex().Replace(redacted, "$1=[REDACTED]");
        return ConnectionStringRegex().Replace(redacted, "$1=[REDACTED]");
    }

    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9._~+/\-]+=*")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?i)(cookie:\s*)[^\r\n]+")]
    private static partial Regex CookieRegex();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|client_secret|app_password|token|access_token|refresh_token|api[_-]?key)\s*=\s*[^;\s,}]+")]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"(?i)\b(Host|Username|User\s+Id|Password|Database)\s*=\s*[^;]+")]
    private static partial Regex ConnectionStringRegex();
}

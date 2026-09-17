using System.Text.RegularExpressions;

namespace NetRatel.Shared.Security;

/// <summary>
/// Defense-in-depth redaction for operator-visible process output. This does
/// not authorize secret access; it prevents common credential forms from being
/// forwarded in a command result when a process prints its environment or
/// connection settings.
/// </summary>
public static class OperatorOutputRedactor
{
    private const string Redacted = "[REDACTED]";

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var redacted = Bearer.Replace(value, "$1" + Redacted);
        redacted = Cookie.Replace(redacted, "$1" + Redacted);
        redacted = SecretAssignment.Replace(redacted, "$1=" + Redacted);
        return ConnectionStringAssignment.Replace(redacted, "$1=" + Redacted);
    }

    private static readonly Regex Bearer = new(@"(?i)(bearer\s+)[A-Za-z0-9._~+/\-]+=*", RegexOptions.Compiled);
    private static readonly Regex Cookie = new(@"(?i)(cookie:\s*)[^\r\n]+", RegexOptions.Compiled);
    private static readonly Regex SecretAssignment = new(@"(?i)\b(password|passwd|pwd|secret|client_secret|app_password|token|access_token|refresh_token|api[_-]?key)\s*=\s*[^;\s,}]+", RegexOptions.Compiled);
    private static readonly Regex ConnectionStringAssignment = new(@"(?i)\b(Host|Username|User\s+Id|Password|Database)\s*=\s*[^;]+", RegexOptions.Compiled);
}

using System.Text.RegularExpressions;

namespace NetRatel.Shared.Contracts.FileSystem;

/// <summary>
/// Manipulates paths that belong to a remote client. These paths must never be
/// interpreted through the operating system hosting the Web or API process.
/// </summary>
public static partial class RemoteFilePath
{
    public static bool TryNormalize(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Contains('\0'))
        {
            return false;
        }

        if (WindowsDrivePath().IsMatch(candidate))
        {
            var drive = char.ToUpperInvariant(candidate[0]);
            var remainder = candidate[2..];
            if (remainder.Length > 0 && remainder[0] is not ('\\' or '/'))
            {
                return false;
            }

            remainder = remainder.Replace('/', '\\');
            if (remainder.Length > 1)
            {
                remainder = remainder.TrimEnd('\\');
            }

            path = remainder.Length == 0 ? $"{drive}:\\" : $"{drive}:{remainder}";
            return true;
        }

        if (candidate.StartsWith("/", StringComparison.Ordinal))
        {
            path = candidate == "/" ? "/" : candidate.TrimEnd('/');
            return true;
        }

        return false;
    }

    public static bool TryGetParent(string? value, out string parent)
    {
        parent = string.Empty;
        if (!TryNormalize(value, out var path) || IsRoot(path))
        {
            return false;
        }

        if (IsWindows(path))
        {
            var separator = path.LastIndexOf('\\');
            parent = separator == 2 ? path[..3] : path[..separator];
            return true;
        }

        var unixSeparator = path.LastIndexOf('/');
        parent = unixSeparator == 0 ? "/" : path[..unixSeparator];
        return true;
    }

    public static bool IsRoot(string? value) =>
        TryNormalize(value, out var path) && (path == "/" || (path.Length == 3 && path[1] == ':' && path[2] == '\\'));

    public static bool IsWindows(string? value) =>
        !string.IsNullOrWhiteSpace(value) && WindowsDrivePath().IsMatch(value.Trim());

    public static string Join(string parent, string child)
    {
        if (!TryNormalize(parent, out var normalizedParent) || string.IsNullOrWhiteSpace(child) || child.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new ArgumentException("A normalized remote parent path and a single child name are required.");
        }

        return IsWindows(normalizedParent)
            ? (IsRoot(normalizedParent) ? normalizedParent : normalizedParent + "\\") + child
            : (normalizedParent == "/" ? "/" : normalizedParent + "/") + child;
    }

    [GeneratedRegex("^[A-Za-z]:")]
    private static partial Regex WindowsDrivePath();
}

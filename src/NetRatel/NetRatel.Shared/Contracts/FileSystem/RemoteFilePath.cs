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
        return TryNormalize(value, out var path) && TryGetNormalizedParent(path, out parent);
    }

    public static bool TryGetDirectoryParent(string? value, out string parent)
    {
        parent = string.Empty;
        return TryNormalizeDirectory(value, out var path) && TryGetNormalizedParent(path, out parent);
    }

    private static bool TryGetNormalizedParent(string path, out string parent)
    {
        parent = string.Empty;
        if (path == "/" || (path.Length == 3 && path[1] == ':' && path[2] == '\\'))
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

    /// <summary>A single folder name is checked using the remote operating system's rules.</summary>
    public static bool TryJoinDirectory(string? parent, string? name, out string path)
    {
        path = string.Empty;
        if (!TryNormalizeDirectory(parent, out var normalizedParent) ||
            string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." ||
            name.Any(char.IsControl) || name.IndexOfAny(['/', '\\']) >= 0)
        {
            return false;
        }

        if (IsWindows(normalizedParent))
        {
            var stem = name.Split('.')[0];
            if (name.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 || name.EndsWith('.') || name.EndsWith(' ') ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
                 (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }
        }

        path = IsWindows(normalizedParent)
            ? (normalizedParent.Length == 3 ? normalizedParent : normalizedParent + "\\") + name
            : (normalizedParent == "/" ? "/" : normalizedParent + "/") + name;
        return path.Length <= 4096;
    }

    public static bool TryNormalizeDirectory(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > 4096 || value.Any(char.IsControl))
        {
            return false;
        }

        // Spaces are part of a Unix filename. Trimming a selected mutation path
        // could otherwise delete a different, similarly named sibling.
        if (value.StartsWith("/", StringComparison.Ordinal))
        {
            if (value.StartsWith("//", StringComparison.Ordinal)) return false;
            path = value == "/" ? "/" : value.TrimEnd('/');
        }
        else if (value != value.Trim() || !TryNormalize(value, out path) || !IsWindows(path))
        {
            return false;
        }

        var separator = IsWindows(path) ? '\\' : '/';
        return !path.Split(separator, StringSplitOptions.RemoveEmptyEntries).Any(component => component is "." or "..");
    }

    [GeneratedRegex("^[A-Za-z]:")]
    private static partial Regex WindowsDrivePath();
}

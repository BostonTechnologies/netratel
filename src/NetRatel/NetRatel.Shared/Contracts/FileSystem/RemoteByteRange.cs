using System.Globalization;

namespace NetRatel.Shared.Contracts.FileSystem;

public readonly record struct RemoteByteRange(long Offset, long Length, bool Partial)
{
    // Multi-range and unknown units are deliberately ignored (200), as HTTP
    // permits. A single unsatisfiable byte range yields 416 without dispatch.
    public static bool TryResolve(string? header, long size, bool ifRangePresent, out RemoteByteRange range)
    {
        range = new(0, size, false);
        if (size < 0) return false;
        if (ifRangePresent || string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(','))
            return true;
        if (header.Length > 128) return false;
        var parts = header[6..].Split('-');
        if (parts.Length != 2 || size == 0) return false;
        if (parts[0].Length == 0)
        {
            if (!Parse(parts[1], out var suffix) || suffix == 0) return false;
            var length = Math.Min(suffix, size);
            range = new(size - length, length, true);
            return true;
        }
        if (!Parse(parts[0], out var start) || start >= size) return false;
        var end = size - 1;
        if (parts[1].Length > 0 && (!Parse(parts[1], out end) || end < start)) return false;
        range = new(start, Math.Min(end, size - 1) - start + 1, true);
        return true;
    }

    private static bool Parse(string value, out long number) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
}

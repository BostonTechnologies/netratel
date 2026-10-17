using System.Globalization;
using System.Text.Json;
using System.Numerics;

namespace NetRatel.Mcp.Core;

/// <summary>Exact bounded identifier parsing for catalogued JSON integer/decimal-string alternatives.</summary>
public static class NetRatelMcpIdentifiers
{
    public static bool TryInt32(JsonElement value, int minimum, int maximum, out int result)
    {
        result = default;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!TryInteger(value, out var number) || number < minimum || number > maximum) return false;
            result = (int)number;
            return true;
        }
        return value.ValueKind == JsonValueKind.String && IsDecimal(value.GetString()) &&
            int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= minimum && result <= maximum;
    }

    public static bool TryInt64(JsonElement value, long minimum, long maximum, out long result)
    {
        result = default;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!TryInteger(value, out var number) || number < minimum || number > maximum) return false;
            result = (long)number;
            return true;
        }
        return value.ValueKind == JsonValueKind.String && IsDecimal(value.GetString()) &&
            long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= minimum && result <= maximum;
    }

    public static bool TryUInt64(JsonElement value, ulong minimum, out ulong result)
    {
        result = default;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!TryInteger(value, out var number) || number < minimum || number > ulong.MaxValue) return false;
            result = (ulong)number;
            return true;
        }
        return value.ValueKind == JsonValueKind.String && IsDecimal(value.GetString()) &&
            ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= minimum;
    }

    private static bool TryInteger(JsonElement value, out BigInteger number)
    {
        number = default;
        var raw = value.GetRawText();
        // Bound the exact parser's work even for exponential JSON numbers.
        // BigInteger rejects every nonzero fractional remainder without
        // floating-point rounding, including beyond JavaScript's safe range.
        if (raw.Length > 256) return false;
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        if (exponentIndex >= 0 && (!int.TryParse(raw.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var exponent) || exponent is < -256 or > 256)) return false;
        return BigInteger.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static bool IsDecimal(string? text) => text is { Length: > 0 and <= 20 } &&
        (text.Length == 1 || text[0] != '0') && text.All(char.IsAsciiDigit);

    internal static string DecimalPattern(ulong maximum, bool allowZero)
    {
        var bound = maximum.ToString(CultureInfo.InvariantCulture);
        var alternatives = new List<string>();
        if (allowZero) alternatives.Add("0");
        if (bound.Length > 1) alternatives.Add($"[1-9][0-9]{{0,{bound.Length - 2}}}");
        for (var index = 0; index < bound.Length; index++)
        {
            var lower = index == 0 ? '1' : '0';
            var upper = (char)(bound[index] - 1);
            if (upper < lower) continue;
            var digit = lower == upper ? lower.ToString() : $"[{lower}-{upper}]";
            var suffix = bound.Length - index - 1;
            alternatives.Add(bound[..index] + digit + (suffix == 0 ? "" : $"[0-9]{{{suffix}}}"));
        }
        alternatives.Add(bound);
        // An explicit end assertion also rejects a trailing line terminator.
        return "^(?:" + string.Join('|', alternatives) + ")(?![\\s\\S])";
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NetRatel.Shared.Contracts.Flows;

/// <summary>Stable v2 data values across JSONB property ordering and exact numeric normalization.</summary>
public static class FlowJsonCanonical
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => SerializeValue(JsonSerializer.SerializeToElement(value, Json));
    public static bool Equal<T>(T left, T right) => Serialize(left) == Serialize(right);
    public static JsonElement Capture(JsonElement value)
    {
        using var document = JsonDocument.Parse(SerializeValue(value));
        return document.RootElement.Clone();
    }
    public static string SerializeValue(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new JsonException("Duplicate JSON data properties are unavailable.");
                writer.WriteStartObject();
                foreach (var property in properties) { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.Number:
                writer.WriteRawValue(Number(value.GetRawText())); break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new JsonException("An undefined JSON value is unavailable.");
        }
    }
    // Avoid binary floating point: all accepted digits are retained. Large exponents are rejected
    // before PostgreSQL can expand a tiny token beyond the bounded data-value budget.
    private static string Number(string raw)
    {
        var negative = raw[0] == '-';
        var text = negative ? raw[1..] : raw;
        var exponentIndex = text.IndexOfAny(['e', 'E']);
        var exponent = 0;
        if (exponentIndex >= 0)
        {
            if (!int.TryParse(text.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent) ||
                exponent is < -4096 or > 4096) throw new JsonException("JSON number expansion exceeds the value budget.");
            text = text[..exponentIndex];
        }
        var point = text.IndexOf('.');
        var position = (point < 0 ? text.Length : point) + exponent;
        var digits = point < 0 ? text : text.Remove(point, 1);
        var leading = 0; while (leading < digits.Length && digits[leading] == '0') leading++;
        if (leading == digits.Length) return "0";
        digits = digits[leading..]; position -= leading;
        digits = digits.TrimEnd('0');
        var length = position <= 0 ? 2L - position + digits.Length : Math.Max(position, digits.Length + (position < digits.Length ? 1L : 0L));
        if (length + (negative ? 1 : 0) > FlowGraphV2Limits.MaximumBindingBytes)
            throw new JsonException("JSON number expansion exceeds the value budget.");
        var number = position <= 0 ? "0." + new string('0', -position) + digits :
            position >= digits.Length ? digits + new string('0', position - digits.Length) : digits.Insert(position, ".");
        return negative ? "-" + number : number;
    }
}

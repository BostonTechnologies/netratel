using System.Text;
using System.Text.Json;

namespace NetRatel.Shared.Contracts.Flows;

/// <summary>Reserve depth for graph/checkpoint/trace wrappers before persisting arbitrary JSON data.</summary>
public static class FlowJsonValueBounds
{
    public static bool Valid(JsonElement value, int maximumBytes, int maximumDepth = FlowGraphV2Limits.MaximumValueDepth)
    {
        if (value.ValueKind == JsonValueKind.Undefined || maximumBytes < 0 || maximumDepth < 1) return false;
        try { return Encoding.UTF8.GetByteCount(value.GetRawText()) <= maximumBytes && Depth(value, maximumDepth); }
        catch (InvalidOperationException) { return false; }
    }
    private static bool Depth(JsonElement value, int remaining)
    {
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return true;
        if (remaining <= 0) return false;
        return value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().All(property => Depth(property.Value, remaining - 1))
            : value.EnumerateArray().All(item => Depth(item, remaining - 1));
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.Flows;

/// <summary>Missing nullable JSON stays absent; a present JSON null remains a captured JsonElement.Null.</summary>
public sealed class FlowNullableJsonElementConverter : JsonConverter<JsonElement?>
{
    public override bool HandleNull => true;
    public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }
    public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
    {
        if (value is { } element) element.WriteTo(writer);
        else writer.WriteNullValue();
    }
}

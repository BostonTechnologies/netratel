using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.API.Models;

public sealed class FlexibleIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt32(),
            JsonTokenType.String => int.TryParse(reader.GetString(), out var value)
                ? value
                : throw new JsonException("Value is not a valid integer."),
            _ => throw new JsonException("Value is not a valid integer.")
        };
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

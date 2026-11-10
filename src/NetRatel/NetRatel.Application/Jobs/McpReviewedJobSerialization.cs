using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Application.Jobs;

/// <summary>Reads protected principal sets without changing established snapshot serialization or digest bytes.</summary>
public static class McpReviewedJobSerialization
{
    private static readonly JsonSerializerOptions ReadOptions = new() { Converters = { new PrincipalSetConverter() } };

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, ReadOptions);

    private sealed class PrincipalSetConverter : JsonConverter<IReadOnlySet<string>>
    {
        public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            (JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? throw new JsonException("A protected principal set is required."))
            .ToHashSet(StringComparer.Ordinal);

        public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value) writer.WriteStringValue(item);
            writer.WriteEndArray();
        }
    }
}

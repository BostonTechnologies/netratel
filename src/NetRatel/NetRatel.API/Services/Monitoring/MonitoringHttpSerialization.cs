using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.API.Services.Monitoring;

/// <summary>Optional omitted immutable collections are empty on the outgoing wire; inbound validation remains unchanged.</summary>
internal static class MonitoringHttpSerialization
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions WriteOptions = CreateWriteOptions();

    public static T NormalizeBounded<T>(T value, int maximumBytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, WriteOptions);
        if (bytes.Length > maximumBytes) throw new MonitoringApiException(413, "monitoring_payload_capacity_exceeded");
        // Return initialized collection values, so ordinary endpoint JSON
        // serialization produces the same bounded shape without a global
        // converter or any change to request deserialization.
        return JsonSerializer.Deserialize<T>(bytes, ReadOptions)!;
    }

    private static JsonSerializerOptions CreateWriteOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new OutgoingImmutableArrayFactory());
        return options;
    }

    private sealed class OutgoingImmutableArrayFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType &&
            typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableArray<>);
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(OutgoingImmutableArray<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
    }

    private sealed class OutgoingImmutableArray<T> : JsonConverter<ImmutableArray<T>>
    {
        public OutgoingImmutableArray() { }
        public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("This converter is only used for bounded outgoing monitoring DTO normalization.");
        public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            if (!value.IsDefault) foreach (var item in value) JsonSerializer.Serialize(writer, item, options);
            writer.WriteEndArray();
        }
    }
}

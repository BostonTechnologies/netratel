using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

/// <summary>Accepts named target enums from tooling while preserving numeric HTTP and protected storage formats.</summary>
public static class JobTargetHttpJson
{
    public static void Configure(Microsoft.AspNetCore.Http.Json.JsonOptions options)
    {
        ConfigureEnums(options.SerializerOptions);
    }

    internal static T Read<T>(JsonElement value)
    {
        var options = new JsonSerializerOptions(NetRatel.Shared.Operations.ManagedOperationCatalog.JsonOptions);
        ConfigureEnums(options);
        return value.Deserialize<T>(options) ?? throw new JsonException("A typed job target request is required.");
    }

    private static void ConfigureEnums(JsonSerializerOptions options)
    {
        // Prefer these three strict boundary readers over any broad enum converter.
        options.Converters.Insert(0, new NumericEnumWithNameReads<JobTargetSource>());
        options.Converters.Insert(0, new NumericEnumWithNameReads<JobRuntimeTargetKind>());
        options.Converters.Insert(0, new NumericEnumWithNameReads<JobNodeFailurePolicy>());
    }

    private sealed class NumericEnumWithNameReads<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value))
                return (TEnum)Enum.ToObject(typeof(TEnum), value);
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 and <= 64 } name &&
                Enum.TryParse<TEnum>(name, true, out var parsed) && Enum.IsDefined(parsed) &&
                string.Equals(name, parsed.ToString(), StringComparison.OrdinalIgnoreCase))
                return parsed;
            throw new JsonException($"A supported {typeof(TEnum).Name} name or numeric value is required.");
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }
}

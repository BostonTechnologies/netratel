using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Shared.Security;

public sealed partial class TaskOutputRedactor
{
    [JsonSourceGenerationOptions(JsonSerializerDefaults.General, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(string))]
    private partial class RedactionJsonContext : JsonSerializerContext;
}

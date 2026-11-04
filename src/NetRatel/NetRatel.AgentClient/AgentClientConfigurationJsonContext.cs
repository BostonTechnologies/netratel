using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.AgentClient;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentClientConfiguration))]
internal partial class AgentClientConfigurationJsonContext : JsonSerializerContext;

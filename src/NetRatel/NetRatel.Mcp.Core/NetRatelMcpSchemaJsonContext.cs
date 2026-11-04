using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NetRatel.Mcp.Core;

[JsonSourceGenerationOptions(JsonSerializerDefaults.General, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JsonObject))]
internal partial class NetRatelMcpSchemaJsonContext : JsonSerializerContext;

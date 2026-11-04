using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Mcp.Core;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(NetRatelToolResponse))]
internal partial class NetRatelMcpToolJsonContext : JsonSerializerContext;

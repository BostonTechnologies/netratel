using System.Text.Json;
using System.Text.Json.Serialization;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.Client.Service.Tasks;

// Keep the gateway's existing Web defaults, including case-insensitive property
// reads and quoted numbers, while making these two known payloads visible to trimming.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ExecShellCommandPayload))]
[JsonSerializable(typeof(ExecLibraryScriptPayload))]
internal partial class TaskPayloadJsonContext : JsonSerializerContext;

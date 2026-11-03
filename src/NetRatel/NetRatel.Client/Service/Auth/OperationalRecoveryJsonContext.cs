using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Client.Service.Auth;

// Retain the existing version-1 field order, explicit nulls and Web number reads.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(OperationalRecoveryState))]
internal partial class OperationalRecoveryJsonContext : JsonSerializerContext;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Client.Service.Updates;

public sealed partial class AkkaClientAutoUpdateCoordinator
{
    // Preserve the released durable-state Web defaults and indented JSON.
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true,
        GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(UpdateRequest))]
    [JsonSerializable(typeof(PolicyState))]
    [JsonSerializable(typeof(UpdateResult))]
    [JsonSerializable(typeof(PendingState))]
    [JsonSerializable(typeof(PendingReady))]
    [JsonSerializable(typeof(ActivationDiagnostic))]
    [JsonSerializable(typeof(PresenceReceipt))]
    [JsonSerializable(typeof(ReadyReceipt))]
    private partial class CoordinatorStateJsonContext : JsonSerializerContext;
}

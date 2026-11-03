using System.Text.Json;
using System.Text.Json.Serialization;
using NetRatel.Shared.Contracts.SoftwareInventory;

namespace NetRatel.Client.Service.SoftwareInventory;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SoftwareScanWork))]
[JsonSerializable(typeof(SoftwareChunkUpload))]
[JsonSerializable(typeof(SoftwareChunkAcknowledgement))]
[JsonSerializable(typeof(SoftwareInventoryWorker.PendingInventory))]
internal partial class SoftwareInventoryJsonContext : JsonSerializerContext;

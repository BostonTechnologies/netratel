using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Client.Service.Updates;

internal sealed partial class ClientUpdateResultDelivery
{
    // The HTTP report uses compact Web defaults; protected state/recovery files
    // retain the existing indented Web representation, including explicit nulls.
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(ResultReport))]
    private partial class ResultReportJsonContext : JsonSerializerContext;

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(NativeResult))]
    [JsonSerializable(typeof(ResumeFence))]
    [JsonSerializable(typeof(RequestContext))]
    [JsonSerializable(typeof(ReadyContext))]
    [JsonSerializable(typeof(ResultReply))]
    [JsonSerializable(typeof(DeliveryRecord))]
    [JsonSerializable(typeof(SuspensionRecord))]
    [JsonSerializable(typeof(IdentityRecoveryReceipt))]
    [JsonSerializable(typeof(JsonElement))]
    private partial class ResultDeliveryJsonContext : JsonSerializerContext;
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Client.Service.Auth;

public sealed partial class AgentCredentialStore
{
    // Released protected files use General defaults: PascalCase, case-sensitive
    // reads, strict numbers, default escaping/order and explicit null fields.
    [JsonSourceGenerationOptions(JsonSerializerDefaults.General, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(CredentialPayload))]
    [JsonSerializable(typeof(ExchangeJournal))]
    private partial class CredentialStoreJsonContext : JsonSerializerContext;
}

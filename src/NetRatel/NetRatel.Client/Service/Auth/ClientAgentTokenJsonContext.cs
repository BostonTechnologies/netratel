using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Client.Service.Auth;

// Keep the existing HTTP Web defaults, including required capabilities, tolerant
// property casing and quoted numbers. Request shapes retain explicit nulls/order.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ClientAgentTokenService.LegacyTokenRequest))]
[JsonSerializable(typeof(ClientAgentTokenService.RefreshExchangeTokenRequest))]
[JsonSerializable(typeof(ClientAgentTokenService.TokenResponse))]
[JsonSerializable(typeof(ClientAgentTokenService.TokenCapabilities))]
internal partial class ClientAgentTokenJsonContext : JsonSerializerContext;

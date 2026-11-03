using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NetRatel.Shared.Security;

// This field order, default escaping, nullable fields and N-format identifiers
// are part of the signed native refresh-v1 protocol.
internal sealed record TokenBodyHashPayload(int? Version, string AgentId, string RefreshToken,
    IReadOnlyList<string>? RequestedScopes, string? ExchangeId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TokenBodyHashPayload))]
internal partial class PopSignatureJsonContext : JsonSerializerContext;

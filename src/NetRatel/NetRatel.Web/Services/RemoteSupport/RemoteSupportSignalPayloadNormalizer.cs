using System.Text.Json;

namespace NetRatel.Web.Services.RemoteSupport;

public static class RemoteSupportSignalPayloadNormalizer
{
    public static RemoteSupportSignalPayload Normalize(string? signalType, string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new RemoteSupportSignalPayload(
                ProviderGeneration: 1,
                PayloadJson: "{}",
                PayloadShape: "empty",
                Provider: null,
                FromProvider: null,
                ToProvider: null,
                HandoverReason: null,
                TopLevelKeys: Array.Empty<string>(),
                PayloadKeys: Array.Empty<string>(),
                HasType: false,
                HasSdp: false,
                SdpLength: 0,
                IsValidSessionDescription: false,
                IsValidIceCandidate: false);
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                TryGetGeneration(root, out var generation) &&
                TryGetPayload(root, out var payload, out var payloadShape))
            {
                return Build(
                    signalType,
                    generation,
                    NormalizePayloadJson(payload),
                    payloadShape,
                    GetString(root, "provider"),
                    GetString(root, "fromProvider"),
                    GetString(root, "toProvider"),
                    GetString(root, "handoverReason"),
                    GetKeys(root));
            }

            if (root.ValueKind == JsonValueKind.String)
            {
                var text = root.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        using var innerDoc = JsonDocument.Parse(text);
                        return Build(
                            signalType,
                            1,
                            innerDoc.RootElement.GetRawText(),
                            "raw_string",
                            null,
                            null,
                            null,
                            null,
                            Array.Empty<string>());
                    }
                    catch
                    {
                    }
                }
            }

            return Build(
                signalType,
                1,
                root.GetRawText(),
                $"raw_{root.ValueKind.ToString().ToLowerInvariant()}",
                GetString(root, "provider"),
                GetString(root, "fromProvider"),
                GetString(root, "toProvider"),
                GetString(root, "handoverReason"),
                GetKeys(root));
        }
        catch
        {
            return new RemoteSupportSignalPayload(
                ProviderGeneration: 1,
                PayloadJson: payloadJson,
                PayloadShape: "raw_invalid_json",
                Provider: null,
                FromProvider: null,
                ToProvider: null,
                HandoverReason: null,
                TopLevelKeys: Array.Empty<string>(),
                PayloadKeys: Array.Empty<string>(),
                HasType: false,
                HasSdp: false,
                SdpLength: 0,
                IsValidSessionDescription: false,
                IsValidIceCandidate: false);
        }
    }

    private static RemoteSupportSignalPayload Build(
        string? signalType,
        int providerGeneration,
        string payloadJson,
        string payloadShape,
        string? provider,
        string? fromProvider,
        string? toProvider,
        string? handoverReason,
        IReadOnlyList<string> topLevelKeys)
    {
        string[] payloadKeys = Array.Empty<string>();
        bool hasType = false;
        bool hasSdp = false;
        int sdpLength = 0;
        bool validDescription = false;
        bool validIce = false;

        try
        {
            using var payloadDoc = JsonDocument.Parse(payloadJson);
            var payload = payloadDoc.RootElement;
            payloadKeys = GetKeys(payload);
            hasType = payload.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String;
            var type = hasType ? typeElement.GetString() : null;
            hasSdp = payload.TryGetProperty("sdp", out var sdpElement) &&
                sdpElement.ValueKind == JsonValueKind.String;
            sdpLength = hasSdp ? sdpElement.GetString()?.Length ?? 0 : 0;
            validDescription = payload.ValueKind == JsonValueKind.Object &&
                (string.Equals(type, "answer", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(type, "offer", StringComparison.OrdinalIgnoreCase)) &&
                hasSdp;
            validIce = payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("candidate", out var candidateElement) &&
                candidateElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(candidateElement.GetString());
        }
        catch
        {
            payloadShape = $"{payloadShape}_invalid_payload_json";
        }

        return new RemoteSupportSignalPayload(
            ProviderGeneration: Math.Max(1, providerGeneration),
            PayloadJson: payloadJson,
            PayloadShape: payloadShape,
            Provider: provider,
            FromProvider: fromProvider,
            ToProvider: toProvider,
            HandoverReason: handoverReason,
            TopLevelKeys: topLevelKeys,
            PayloadKeys: payloadKeys,
            HasType: hasType,
            HasSdp: hasSdp,
            SdpLength: sdpLength,
            IsValidSessionDescription: validDescription,
            IsValidIceCandidate: validIce);
    }

    private static bool TryGetGeneration(JsonElement root, out int generation)
    {
        generation = 1;
        if (!root.TryGetProperty("providerGeneration", out var generationElement))
        {
            return false;
        }

        generation = generationElement.TryGetInt32(out var parsed)
            ? Math.Max(1, parsed)
            : 1;
        return true;
    }

    private static bool TryGetPayload(JsonElement root, out JsonElement payload, out string payloadShape)
    {
        if (root.TryGetProperty("payload", out payload))
        {
            payloadShape = payload.ValueKind == JsonValueKind.String
                ? "wrapped_payload_string"
                : $"wrapped_payload_{payload.ValueKind.ToString().ToLowerInvariant()}";
            return true;
        }

        if (root.TryGetProperty("payloadJson", out payload))
        {
            payloadShape = "wrapped_payloadJson_string";
            return true;
        }

        payload = default;
        payloadShape = "wrapped_missing_payload";
        return false;
    }

    private static string NormalizePayloadJson(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.String)
        {
            var text = payload.GetString();
            return string.IsNullOrWhiteSpace(text) ? "{}" : text;
        }

        return payload.GetRawText();
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(propertyName, out var element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string[] GetKeys(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Select(x => x.Name).ToArray()
            : Array.Empty<string>();
}

public sealed record RemoteSupportSignalPayload(
    int ProviderGeneration,
    string PayloadJson,
    string PayloadShape,
    string? Provider,
    string? FromProvider,
    string? ToProvider,
    string? HandoverReason,
    IReadOnlyList<string> TopLevelKeys,
    IReadOnlyList<string> PayloadKeys,
    bool HasType,
    bool HasSdp,
    int SdpLength,
    bool IsValidSessionDescription,
    bool IsValidIceCandidate)
{
    public string ToDiagnosticJson(string signalType, string code) =>
        JsonSerializer.Serialize(new
        {
            code,
            statusCode = code,
            signalType,
            providerGeneration = ProviderGeneration,
            payloadShape = PayloadShape,
            hasType = HasType,
            hasSdp = HasSdp,
            sdpLength = SdpLength,
            topLevelKeys = TopLevelKeys,
            payloadKeys = PayloadKeys,
            provider = Provider,
            fromProvider = FromProvider,
            toProvider = ToProvider,
            handoverReason = HandoverReason
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

/// <summary>Allowlisted, bounded wire details. Raw frames and private thinking never enter durable trace.</summary>
public static class NetclawTraceMapper
{
    private static readonly Regex SensitiveKey = new("password|passwd|pwd|jwt|secret|token|credential|authorization|cookie|connection.?string|private.?key|api.?key|idempotency.?key|pairing.?code|enrollment.?code|device.?code|user.?code", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Secrets = new("(?i)(bearer\\s+)[a-z0-9._~+/=-]+|((?:password|passwd|pwd|secret|token|api[_-]?key|authorization|cookie)\\s*[:=]\\s*)[^\\s,;]+|-----BEGIN [^-]*PRIVATE KEY-----[\\s\\S]*?-----END [^-]*PRIVATE KEY-----|(?<=://)[^\\s/@:]+:[^\\s/@]+@", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly string[] NumericFields = ["inputTokens", "outputTokens", "totalTokens", "cachedInputTokens", "reasoningTokens", "contextWindowTokens", "usagePercent", "promptMs", "predictedPerSecond", "messagesBefore", "messagesAfter", "preCompactionInputTokens", "keepCountUsed", "durationMs", "toolCountSub", "findingsCount", "turnNumber"];
    private static readonly string[] BoolFields = ["isProcessing", "processingStateRequired", "summarized", "toolResultsCleared", "subAgentSuccess", "interactionIsMessy", "interactionHasAdoptedContext", "interactionHasThirdPartyAdoptedContext"];
    private static readonly string[] TextFields = ["agentName", "phase", "subAgentOutcome", "subAgentOutcomeReason", "fileName", "filePath", "mimeType", "errorMessage", "errorCategory", "errorCorrelationId", "interactionKind", "interactionDisplayText", "interactionNotes", "interactionCwd", "interactionLifetime", "interactionCloseKind", "turnOutcome"];

    public static DateTimeOffset? SourceTimestamp(JsonElement output)
    {
        if (output.TryGetProperty("timestampMs", out var timestamp) && timestamp.ValueKind == JsonValueKind.Number
            && timestamp.TryGetInt64(out var milliseconds) && milliseconds > 0)
            try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); } catch (ArgumentOutOfRangeException) { /* Invalid source times remain unavailable. */ }
        return null;
    }

    public static NetclawTraceDto Map(JsonElement output, string sessionId, Guid? messageId, params string[] knownSecrets)
    {
        bool redacted = false, truncated = false;
        string Clean(string text, int limit = 8192)
        {
            foreach (var secret in knownSecrets.Where(x => !string.IsNullOrEmpty(x)))
            {
                if (text.Contains(secret, StringComparison.Ordinal)) { text = text.Replace(secret, "[redacted]", StringComparison.Ordinal); redacted = true; }
            }
            try
            {
                var existing = NetRatel.Shared.Security.OperatorOutputRedactor.Redact(text);
                redacted |= existing != text; text = existing;
                var safe = Secrets.Replace(text, match => match.Groups[1].Success ? match.Groups[1].Value + "[redacted]" : match.Groups[2].Success ? match.Groups[2].Value + "[redacted]" : "[redacted]");
                redacted |= safe != text; text = safe;
            }
            catch (RegexMatchTimeoutException) { redacted = true; text = "[redacted: content could not be safely inspected]"; }
            if (text.Length > limit) { truncated = true; text = text[..limit] + "\n[truncated]"; }
            return text;
        }
        JsonNode? Sanitize(JsonNode? node, int depth = 0)
        {
            if (depth > 12) { truncated = true; return JsonValue.Create("[truncated: nesting]"); }
            if (node is JsonObject obj)
            {
                var result = new JsonObject();
                foreach (var item in obj.Take(64))
                {
                    bool sensitive;
                    try { sensitive = SensitiveKey.IsMatch(item.Key); } catch (RegexMatchTimeoutException) { sensitive = true; }
                    if (sensitive) { result[Clean(item.Key, 128)] = "[redacted]"; redacted = true; }
                    else result[Clean(item.Key, 128)] = Sanitize(item.Value, depth + 1);
                }
                if (obj.Count > 64) truncated = true;
                return result;
            }
            if (node is JsonArray array) { if (array.Count > 64) truncated = true; return new JsonArray(array.Take(64).Select(x => Sanitize(x, depth + 1)).ToArray()); }
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                if (text.Length <= 65536 && (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')))
                    try { return Sanitize(JsonNode.Parse(text), depth + 1); } catch (JsonException) { /* Plain text result. */ }
                return JsonValue.Create(Clean(text));
            }
            return node?.DeepClone();
        }
        string? Payload(string field, bool requireJson = false)
        {
            if (!output.TryGetProperty(field, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            var raw = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            if (raw.Length > 65536) { truncated = true; return "[unavailable: payload exceeds safe inspection limit]"; }
            try { return Clean(Sanitize(JsonNode.Parse(raw))?.ToJsonString(new() { WriteIndented = true }) ?? "null"); }
            catch (JsonException) { return requireJson ? "[unavailable: malformed JSON]" : Clean(raw); }
        }
        string? String(string name, int limit = 256) => output.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Clean(value.GetString()!, limit) : null;
        var sourceTime = SourceTimestamp(output);
        var type = String("type");
        var details = new JsonObject();
        foreach (var name in NumericFields)
            if ((name != "durationMs" || type == "subagent") && output.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number) details[name] = JsonNode.Parse(number.GetRawText());
        foreach (var name in BoolFields)
            if (output.TryGetProperty(name, out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False) details[name] = flag.GetBoolean();
        foreach (var name in TextFields)
            if (String(name, 512) is { } text) details[name] = text;
        foreach (var name in new[] { "interactionPatterns", "interactionCandidateVerbs" })
            if (output.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array) details[name] = Sanitize(JsonNode.Parse(list.GetRawText()));
        var failure = String("toolFailureCode");
        var arguments = Payload("argumentsJson", true);
        var resultBody = Payload("result");
        var detailsJson = details.Count == 0 ? null : Clean(details.ToJsonString(new() { WriteIndented = true }), 8192);
        return new(String("toolName", 128), type == "tool_call" ? "started" : type == "tool_result" ? string.IsNullOrWhiteSpace(failure) ? "result received" : "reported failure" : null,
            sourceTime, sessionId, messageId, arguments, resultBody, failure, detailsJson, redacted, truncated);
    }
}

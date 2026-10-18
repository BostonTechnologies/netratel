using System.Text.Json;
using System.Text.Json.Nodes;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.Shared.Security;

/// <summary>Value-based masking before native output leaves the execution boundary.</summary>
public sealed class TaskOutputRedactor
{
    public const string Mask = "[REDACTED]";
    public const string ReceiptProperty = "outputProtectionBinding";
    private readonly string[] _values;
    private readonly bool _withhold;

    public string? Binding { get; }

    public TaskOutputRedactor(TaskOutputPolicy? policy, IReadOnlyDictionary<string, string>? parameters = null,
        IReadOnlyDictionary<string, string>? additionalSecretValues = null, Func<string, string?>? resolveEnvironment = null)
    {
        Binding = policy?.Binding;
        var withhold = policy?.WithholdOutput == true;
        var values = new List<string>(additionalSecretValues?.Values ?? []);
        if (policy is not null)
        {
            resolveEnvironment ??= Environment.GetEnvironmentVariable;
            foreach (var name in policy.SecretParameters)
            {
                if (TryGetParameter(parameters, name, out var value) || TryGetParameter(additionalSecretValues, name, out value)) values.Add(value);
                else withhold = true;
            }
            foreach (var name in policy.SecretReferenceParameters)
            {
                if (TryGetParameter(parameters, name, out var reference)) Resolve(reference);
                else withhold = true;
            }
            foreach (var reference in policy.EnvironmentReferences) Resolve(reference);

            void Resolve(string reference)
            {
                var value = resolveEnvironment(reference);
                if (value is null) withhold = true;
                else values.Add(value);
            }
        }
        // The runner publishes lines. Retain each nonempty multiline fragment
        // as well, so newline splitting cannot reveal part of a secret value.
        _values = values.Where(x => !string.IsNullOrEmpty(x))
            .SelectMany(x => new[] { x }.Concat(x.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)))
            .Distinct(StringComparer.Ordinal).OrderByDescending(x => x.Length).ToArray();
        _withhold = withhold;
    }

    public string? Redact(string? value) => value is null ? null : _withhold ? Withhold(value) : Scrub(value, 0);

    public string? RedactEnvelope(string? value)
    {
        if (value is null) return null;
        if (_withhold) return WithholdEnvelope(value);
        try
        {
            if (JsonNode.Parse(value) is not JsonObject envelope) return Redact(value);
            var changed = false;
            foreach (var pair in envelope.ToArray())
            {
                if (IsLifecycleValue(pair.Key, pair.Value)) continue;
                if (pair.Value is null) continue;
                var original = pair.Value.ToJsonString();
                var safe = Scrub(original, 0);
                if (safe == original) continue;
                changed = true;
                envelope[pair.Key] = JsonNode.Parse(safe);
            }
            return changed ? envelope.ToJsonString() : value;
        }
        catch (JsonException) { return Redact(value); }
    }

    public static string? ReadBinding(string? value)
    {
        try
        {
            using var document = JsonDocument.Parse(value ?? "null");
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(ReceiptProperty, out var binding) && binding.ValueKind == JsonValueKind.String
                ? binding.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    public static string? Withhold(string? value) => value is null || value.Length == 0 ? value : Mask;

    /// <summary>Retain only typed lifecycle facts; arbitrary output is unavailable without classification evidence.</summary>
    public static string? WithholdEnvelope(string? value)
    {
        if (value is null || value.Length == 0) return value;
        try
        {
            if (JsonNode.Parse(value) is not JsonObject envelope) return Mask;
            var safe = new JsonObject();
            foreach (var pair in envelope)
                if (IsLifecycleValue(pair.Key, pair.Value)) safe[pair.Key] = pair.Value?.DeepClone();
                else if (pair.Key is "stdout" or "stderr")
                    safe[pair.Key] = pair.Value is JsonArray lines
                        ? new JsonArray(lines.Select(_ => (JsonNode?)JsonValue.Create(Mask)).ToArray()) : JsonValue.Create(Mask);
            // Never copy unknown property names, numeric output, diagnostics or
            // a protection receipt that did not prove this dispatch binding.
            safe["outputRedacted"] = true;
            if (!safe.ContainsKey("stdout") && !safe.ContainsKey("stderr")) safe["message"] = Mask;
            return safe.ToJsonString();
        }
        catch (JsonException) { return Mask; }
    }

    private string Scrub(string value, int depth)
    {
        if (depth >= 16) return JsonSerializer.Serialize(Mask);
        try
        {
            var node = JsonNode.Parse(value);
            if (node is null) return Replace(value);
            if (node is JsonValue scalar)
            {
                if (scalar.TryGetValue<string>(out var text))
                {
                    var safe = ScrubText(text, depth + 1);
                    return safe == text ? value : JsonSerializer.Serialize(safe);
                }
                return IsSecretScalar(scalar) ? JsonSerializer.Serialize(Mask) : value;
            }
            var changed = false;
            Visit(node);
            return changed ? node.ToJsonString() : value;

            void Visit(JsonNode candidate)
            {
                if (candidate is JsonObject obj)
                    foreach (var pair in obj.ToArray())
                    {
                        var key = Replace(pair.Key);
                        if (key != pair.Key)
                        {
                            changed = true;
                            obj.Remove(pair.Key);
                            key = Mask;
                            while (obj.ContainsKey(key)) key += "_";
                            obj[key] = pair.Value;
                        }
                        if (pair.Value is JsonValue item) obj[key] = ProtectScalar(item);
                        else if (pair.Value is not null) Visit(pair.Value);
                    }
                else if (candidate is JsonArray array)
                    for (var index = 0; index < array.Count; index++)
                        if (array[index] is JsonValue item) array[index] = ProtectScalar(item);
                        else if (array[index] is { } child) Visit(child);
            }

            JsonNode ProtectScalar(JsonValue item)
            {
                if (item.TryGetValue<string>(out var text))
                {
                    var safe = ScrubText(text, depth + 1);
                    changed |= safe != text;
                    return JsonValue.Create(safe)!;
                }
                if (!IsSecretScalar(item)) return item.DeepClone();
                changed = true;
                return JsonValue.Create(Mask)!;
            }
        }
        catch (JsonException) { return Replace(value); }
    }

    private string ScrubText(string text, int depth)
    {
        // Decode before replacing: a numeric secret inside nested JSON must be
        // replaced as a quoted JSON string, without invalidating the document.
        var trimmed = text.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '{' or '[' or '"' ? Scrub(text, depth) : Replace(text);
    }

    private bool IsSecretScalar(JsonValue scalar) => _values.Contains(scalar.ToJsonString(), StringComparer.Ordinal);

    private string Replace(string value)
    {
        foreach (var secret in _values) value = value.Replace(secret, Mask, StringComparison.Ordinal);
        return OperatorOutputRedactor.Redact(value);
    }

    private static bool IsLifecycleValue(string key, JsonNode? value) => value is JsonValue scalar &&
        (key is "exitCode" or "outputSequence" && scalar.TryGetValue<long>(out _) ||
         key is "outputTruncated" or "outputStreamed" or "processStarted" or "cancelled" or "timedOut" or "outcomeUnknown" && scalar.TryGetValue<bool>(out _));

    private static bool TryGetParameter(IReadOnlyDictionary<string, string>? parameters, string name, out string value)
    {
        var pair = parameters?.FirstOrDefault(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        value = pair?.Value ?? string.Empty;
        return pair?.Key is not null;
    }
}

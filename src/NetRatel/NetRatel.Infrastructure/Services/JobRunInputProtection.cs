using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>Server-owned immutable submission metadata and protected execution material.</summary>
internal sealed class JobRunInputProtection(IDataProtectionProvider protection)
{
    internal const string Mask = "[REDACTED]";
    internal sealed record Parameter(long Id, string Name, string Type, bool Required);
    internal sealed record Sensitivity(string Provenance, string? DefinitionRevision, Parameter[] Parameters,
        string? PriorTerminalResultHash = null, string? ProtectedTerminalResultHash = null)
    {
        public bool Legacy => Provenance != "submitted-definition-v1";
        public HashSet<string> Secrets => Parameters.Where(p => p.Type.Equals("secret", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    internal sealed record Execution(string? InputsJson, string? OptionsJson);

    public static Sensitivity Capture(JobDefinition? definition, IEnumerable<JobParameterDefinition> parameters)
    {
        var snapshot = parameters.OrderBy(p => p.Id).Select(p => new Parameter(p.Id, p.Name, p.Type, p.Required)).ToArray();
        var revision = definition is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { definition.Id, definition.UpdatedAtUtc, Parameters = snapshot }))));
        return new(definition is null ? "legacy-unknown-v1" : "submitted-definition-v1", revision, snapshot);
    }

    public static Sensitivity Metadata(JobRunRecord run)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<Sensitivity>(run.InputSensitivityJson ?? "null");
            if (metadata is { Provenance: "submitted-definition-v1", DefinitionRevision.Length: 64, Parameters: not null }
                && metadata.Parameters.All(p => p is not null && !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Type)))
                return metadata;
            if (metadata is { Provenance: "legacy-unknown-v1" }) return metadata with { Parameters = [] };
            return new("legacy-unknown-v1", null, []);
        }
        catch (JsonException) { return new("legacy-unknown-v1", null, []); }
    }

    public void Store(JobRunRecord run, Sensitivity metadata, string? inputs, string? options)
    {
        run.InputSensitivityJson = JsonSerializer.Serialize(metadata);
        run.ProtectedExecutionJson = Protector(run).Protect(JsonSerializer.Serialize(new Execution(inputs, options)));
        run.InputsJson = RedactInputs(inputs, metadata);
        run.OptionsJson = RedactOptions(options, metadata, SecretValues(inputs, metadata));
    }

    public Execution ForExecution(JobRunRecord run)
    {
        if (run.ProtectedExecutionJson is null) return new(run.InputsJson, run.OptionsJson); // Unmigrated history: execution only.
        try { return JsonSerializer.Deserialize<Execution>(Protector(run).Unprotect(run.ProtectedExecutionJson))
                ?? throw new CryptographicException(); }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        { throw new InvalidOperationException("Protected run execution material is unavailable; restore the original Data Protection key ring."); }
    }

    public string? Diagnostic(JobRunRecord run, string? value)
    {
        if (value is null) return null;
        var metadata = Metadata(run);
        if (metadata.Legacy) return value.Length == 0 ? value : Mask;
        if (metadata.Secrets.Count == 0) return value;
        var execution = ForExecution(run);
        return Scrub(value, SecretValues(execution.InputsJson, metadata), scrubKeys: true);
    }

    public static string? RedactInputs(string? value, Sensitivity metadata)
    {
        if (value is null) return null;
        try
        {
            var node = JsonNode.Parse(value);
            if (node is not JsonObject root) return JsonSerializer.Serialize(Mask);
            Redact(root);
            return Scrub(root.ToJsonString(), SecretValues(value, metadata));
            void Redact(JsonObject obj)
            {
                foreach (var item in obj.ToArray())
                {
                    if (metadata.Legacy)
                    {
                        if (item.Value is JsonObject legacyObject) Redact(legacyObject);
                        else if (item.Value is JsonArray legacyArray) MaskArray(legacyArray);
                        else obj[item.Key] = Mask;
                    }
                    else if (metadata.Secrets.Contains(item.Key)) obj[item.Key] = Mask;
                    else if (item.Key.Equals("input", StringComparison.OrdinalIgnoreCase) && item.Value is JsonObject nested) Redact(nested);
                }
            }
            void MaskArray(JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is JsonObject obj) Redact(obj);
                    else if (array[i] is JsonArray nested) MaskArray(nested);
                    else array[i] = Mask;
            }
        }
        catch (JsonException) { return JsonSerializer.Serialize(Mask); }
    }

    public static string? RedactOptions(string? value, Sensitivity metadata, IReadOnlyList<string> secrets)
    {
        if (value is null) return null;
        try
        {
            var node = JsonNode.Parse(value);
            if (metadata.Legacy)
            {
                if (node is JsonArray historical)
                {
                    for (var index = 0; index < historical.Count; index++)
                        if (historical[index] is JsonObject option)
                            foreach (var field in option.ToArray().Where(p => !p.Key.Equals("name", StringComparison.OrdinalIgnoreCase))) option[field.Key] = Mask;
                        else historical[index] = Mask;
                    return historical.ToJsonString();
                }
                return RedactInputs(value, metadata);
            }
            if (node is JsonArray array)
            {
                foreach (var option in array.OfType<JsonObject>())
                {
                    var name = option.FirstOrDefault(p => p.Key.Equals("name", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();
                    if (metadata.Legacy || (name is not null && metadata.Secrets.Contains(name)))
                        foreach (var field in option.ToArray().Where(p => p.Key.Equals("value", StringComparison.OrdinalIgnoreCase))) option[field.Key] = Mask;
                }
            }
            else if (node is JsonObject obj)
            {
                foreach (var field in obj.ToArray())
                    if (metadata.Legacy || metadata.Secrets.Contains(field.Key)) obj[field.Key] = Mask;
            }
            return Scrub(node?.ToJsonString() ?? "null", secrets);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    private static IReadOnlyList<string> SecretValues(string? inputs, Sensitivity metadata)
    {
        var values = new List<string>();
        try
        {
            if (JsonNode.Parse(inputs ?? "null") is JsonObject root)
            {
                Collect(root);
                if (root.FirstOrDefault(p => p.Key.Equals("input", StringComparison.OrdinalIgnoreCase)).Value is JsonObject nested) Collect(nested);
            }
            void Collect(JsonObject obj)
            {
                foreach (var pair in obj)
                    if (metadata.Legacy || metadata.Secrets.Contains(pair.Key))
                    {
                        var text = pair.Value is JsonValue scalar && scalar.TryGetValue<string>(out var str) ? str : pair.Value?.ToJsonString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            values.Add(text);
                            if (!string.IsNullOrWhiteSpace(text)) values.Add(text.Trim());
                        }
                    }
            }
        }
        catch (JsonException) { }
        return values.Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();
    }

    private static string Scrub(string value, IReadOnlyList<string> secrets, bool scrubKeys = false)
    {
        // A secret can itself be valid JSON (including quoted text). Compare the
        // original text before decoding diagnostic JSON to avoid changing its spelling.
        if (secrets.Contains(value, StringComparer.Ordinal)) return Mask;
        // Scrub decoded JSON string nodes as well as plain diagnostics, so escaped Unicode,
        // quotes and newlines cannot bypass the same value-based redaction.
        try
        {
            var node = JsonNode.Parse(value);
            if (scrubKeys && HasSensitiveKey(node)) return Mask;
            if (node is JsonValue rootScalar && rootScalar.TryGetValue<string>(out var rootText))
                return JsonSerializer.Serialize(Replace(rootText));
            // A log/error is text even when it happens to be a valid JSON scalar.
            if (node is JsonValue) return Replace(value);
            if (node is not null) { Visit(node); return node.ToJsonString(); }
            bool HasSensitiveKey(JsonNode? candidate) => candidate switch
            {
                JsonObject obj => obj.Any(pair => Replace(pair.Key) != pair.Key || HasSensitiveKey(pair.Value)),
                JsonArray array => array.Any(HasSensitiveKey),
                _ => false
            };
            void Visit(JsonNode node)
            {
                if (node is JsonObject obj)
                    foreach (var pair in obj.ToArray())
                    {
                        if (pair.Value is JsonValue scalar && scalar.TryGetValue<string>(out var str)) obj[pair.Key] = Replace(str);
                        else if (pair.Value is not null) Visit(pair.Value);
                    }
                else if (node is JsonArray array)
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var str)) array[i] = Replace(str);
                        else if (array[i] is { } child) Visit(child);
                    }
            }
        }
        catch (JsonException) { }
        return Replace(value);
        string Replace(string text)
        {
            foreach (var secret in secrets) text = text.Replace(secret, Mask, StringComparison.Ordinal);
            return text;
        }
    }

    private IDataProtector Protector(JobRunRecord run) => protection.CreateProtector("NetRatel.JobRun.Execution.v1",
        run.TenantId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "global",
        run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), run.JobId.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

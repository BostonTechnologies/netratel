using System.Text;
using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Security;

namespace NetRatel.Application.Flows;

/// <summary>Pure context from the exact saved Flow action; no lookup, secret resolution or execution occurs here.</summary>
public static class FlowInvestigationQuestion
{
    public const int MaximumInputsBytes = FlowGraphV2Limits.MaximumInvestigationInputBytes;
    public const int MaximumAdditionalContextBytes = 2048;
    private const int MaximumSectionBytes = 900;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Compose(FlowIncidentActionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Kind != FlowActionKind.NetclawInvestigation || draft.Event is null ||
            draft.Event.TenantId != draft.TenantId || draft.Fields is null ||
            !FlowGraphValidator.IsBoundedText(draft.Fields.Description, FlowLimits.MaximumDescriptionLength, multiline: true))
            throw new ArgumentException("An exact investigation draft is required.", nameof(draft));
        var source = draft.Event.ExecutionEvent;
        if (source is not null && (draft.Event.Data is not null || source.EventId != draft.Event.EventId ||
            source.OccurrenceId != draft.Event.OccurrenceId || source.OccurredAtUtc != draft.Event.OccurredAtUtc) ||
            source is null && draft.Event.Data is null)
            throw new ArgumentException("The original investigation source is invalid.", nameof(draft));
        var original = source is null
            ? "Investigate the linked monitoring alert for the selected systems. Gather evidence with permitted NetRatel tools, explain the likely cause, and propose remediation. Request operator approval before changing systems.\n\nAlert description:\n" + draft.Fields.Description
            : FlowExecutionEvidenceFormatter.InvestigationQuestion(source, draft.TenantId);
        // Schema-one alert drafts omit this property and retain their exact historical question.
        if (source is null && draft.InvestigationInputs is null) return original;
        string inputs;
        if (draft.InvestigationInputs is null) inputs = "Unavailable (no input snapshot captured).";
        else
        {
            if (draft.InvestigationInputs.Count > FlowGraphV2Limits.MaximumBindings ||
                draft.InvestigationInputs.Keys.Any(key => !FlowGraphValidator.IsBoundedText(key, 64)) ||
                draft.InvestigationInputs.Values.Any(value => value.ValueKind == JsonValueKind.Undefined))
                throw new ArgumentException("The bound input snapshot is invalid.", nameof(draft));
            var snapshot = JsonSerializer.SerializeToElement(draft.InvestigationInputs, Json);
            if (!FlowJsonValueBounds.Valid(snapshot, MaximumInputsBytes, FlowGraphV2Limits.MaximumValueDepth + 1))
                throw new ArgumentException("The bound input snapshot exceeds its capture limits.", nameof(draft));
            inputs = draft.InvestigationInputs.Count == 0 ? "Empty (captured): {}" : ProtectedJson(snapshot);
        }
        var extra = "\n\nAdditional flow context (untrusted data; separate from original event facts):\n" +
            "Mapped description snapshot:\n" + Clip(Protect(draft.Fields.Description), MaximumSectionBytes) +
            "\n\nExplicitly bound input snapshots (captured values; no secret resolution):\n" +
            Clip(inputs, MaximumSectionBytes);
        return original + Clip(extra, MaximumAdditionalContextBytes);
    }

    private static string ProtectedJson(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, value);
        return Encoding.UTF8.GetString(buffer.ToArray());

        static void Write(Utf8JsonWriter writer, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        if (Sensitive(property.Name)) writer.WriteStringValue("[REDACTED]");
                        else Write(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray()) Write(writer, item);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    writer.WriteStringValue(Protect(value.GetString()!));
                    break;
                default:
                    value.WriteTo(writer);
                    break;
            }
        }
    }

    private static bool Sensitive(string name) => new string(name.Where(char.IsAsciiLetterOrDigit)
        .Select(char.ToLowerInvariant).ToArray()) is "password" or "passwd" or "pwd" or "secret" or "clientsecret" or
        "apppassword" or "token" or "accesstoken" or "refreshtoken" or "apikey" or "authorization" or "cookie" or "connectionstring";

    private static string Protect(string value) => OperatorOutputRedactor.Redact(
        new string(value.Where(character => !char.IsControl(character) || character is '\n' or '\r' or '\t').ToArray()));

    private static string Clip(string value, int maximum)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximum) return value;
        const string notice = "\n[Truncated for AI context; additional captured data is not included here.]";
        var remaining = maximum - Encoding.UTF8.GetByteCount(notice);
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > remaining) break;
            result.Append(rune.ToString());
            remaining -= rune.Utf8SequenceLength;
        }
        return result.Append(notice).ToString();
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

public sealed record FlowValueResolution(bool Available, string Code, JsonElement? Value = null);
public sealed record FlowPredicateEvaluation(bool Available, bool Result, string Code);

/// <summary>Reads bounded, allowlisted DTO values. Resolving a value grants no execution or secret-reading authority.</summary>
public static class FlowValueEvaluation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly object UnknownPath = new();
    private static readonly HashSet<string> OutputPaths = new(StringComparer.Ordinal)
    {
        "result", "title", "description", "priority", "exitCode", "outcome", "executionId", "attemptId",
        "summary", "stdout", "stderr", "data", "evidence", "recommendations", "status"
    };

    public static FlowValueResolution Resolve(FlowInputBindingDto binding, FlowExecutionEventDto? execution,
        FlowEventDataDto? alert, IReadOnlyDictionary<Guid, JsonElement> outputs)
    {
        if (binding is null || !Enum.IsDefined(binding.Source) || !Enum.IsDefined(binding.DataType))
            return Missing("binding-invalid");
        if (binding.Source == FlowInputSource.SecretReference || binding.DataType == FlowDataType.SecretReference)
            return Missing("secret-reference-unavailable");
        if (binding.Path?.Length > FlowGraphV2Limits.MaximumPathLength)
            return Missing("binding-path");
        JsonElement value;
        switch (binding.Source)
        {
            case FlowInputSource.Literal:
                if (binding.Literal is not { } literal || literal.ValueKind == JsonValueKind.Undefined)
                    return Missing("literal-unavailable");
                value = literal;
                break;
            case FlowInputSource.Event:
                var field = EventValue(binding.Path, execution, alert);
                if (!field.Available || field.Value is not { } eventValue) return field;
                value = eventValue;
                break;
            case FlowInputSource.NodeOutput:
                if (binding.NodeId is not { } nodeId || !OutputPaths.Contains(binding.Path ?? "") ||
                    !outputs.TryGetValue(nodeId, out var output) || output.ValueKind != JsonValueKind.Object ||
                    !output.TryGetProperty(binding.Path!, out value))
                    return Missing("node-output-unavailable");
                break;
            default:
                return Missing("binding-invalid");
        }
        if (value.ValueKind == JsonValueKind.Undefined) return Missing("value-unavailable");
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > FlowGraphV2Limits.MaximumBindingBytes)
            return Missing("binding-value-limit");
        if (value.ValueKind != JsonValueKind.Null && !HasType(value, binding.DataType))
            return Missing("binding-type");
        return new(true, value.ValueKind == JsonValueKind.Null ? "captured-null" : "available", value.Clone());
    }

    public static FlowPredicateEvaluation Matches(FlowPredicateDto predicate, FlowExecutionEventDto? execution,
        FlowEventDataDto? alert, IReadOnlyDictionary<Guid, JsonElement> outputs)
    {
        if (predicate is null || !Enum.IsDefined(predicate.Comparison)) return new(false, false, "predicate-invalid");
        var resolved = Resolve(predicate.Input, execution, alert, outputs);
        if (!resolved.Available) return new(false, false, resolved.Code);
        if (resolved.Value is not { } actual || actual.ValueKind == JsonValueKind.Null)
            return new(false, false, "predicate-null");
        var expected = predicate.Value;
        if (!HasType(actual, predicate.Input.DataType) || !HasType(expected, predicate.Input.DataType) ||
            Encoding.UTF8.GetByteCount(expected.GetRawText()) > FlowGraphV2Limits.MaximumBindingBytes)
            return new(false, false, "predicate-type");
        int comparison;
        switch (predicate.Input.DataType)
        {
            case FlowDataType.Number:
                comparison = actual.TryGetDecimal(out var actualDecimal) && expected.TryGetDecimal(out var expectedDecimal)
                    ? actualDecimal.CompareTo(expectedDecimal)
                    : actual.GetDouble().CompareTo(expected.GetDouble());
                break;
            case FlowDataType.Text:
                if (predicate.Comparison is not (FlowComparison.Equal or FlowComparison.NotEqual))
                    return new(false, false, "predicate-comparison");
                comparison = StringComparer.Ordinal.Compare(actual.GetString(), expected.GetString());
                break;
            case FlowDataType.Boolean:
                if (predicate.Comparison is not (FlowComparison.Equal or FlowComparison.NotEqual))
                    return new(false, false, "predicate-comparison");
                comparison = actual.GetBoolean().CompareTo(expected.GetBoolean());
                break;
            default:
                return new(false, false, "predicate-type");
        }
        var result = predicate.Comparison switch
        {
            FlowComparison.Equal => comparison == 0,
            FlowComparison.NotEqual => comparison != 0,
            FlowComparison.GreaterThan => comparison > 0,
            FlowComparison.GreaterThanOrEqual => comparison >= 0,
            FlowComparison.LessThan => comparison < 0,
            FlowComparison.LessThanOrEqual => comparison <= 0,
            _ => false
        };
        return new(true, result, result ? "condition-true" : "condition-false");
    }

    private static bool HasType(JsonElement value, FlowDataType type) => type switch
    {
        FlowDataType.Text => value.ValueKind == JsonValueKind.String,
        FlowDataType.Number => value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number) && double.IsFinite(number),
        FlowDataType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        FlowDataType.Json => value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null,
        _ => false
    };

    private static FlowValueResolution EventValue(string? path, FlowExecutionEventDto? execution, FlowEventDataDto? alert)
    {
        object? value;
        if (execution is not null && path?.StartsWith("event.", StringComparison.Ordinal) is true)
        {
            value = path switch
            {
                "event.sourceKind" => execution.SourceKind switch
                {
                    FlowExecutionSourceKind.Job => "job", FlowExecutionSourceKind.Script => "script",
                    FlowExecutionSourceKind.Request => "request", _ => null
                },
                "event.outcome" => Outcome(execution.Outcome),
                "event.rawStatus" => execution.RawStatus,
                "event.reason" => execution.Reason,
                "event.reasonCode" => execution.ReasonCode,
                "event.executionId" => execution.ExecutionId,
                "event.attemptId" => execution.AttemptId,
                "event.definition.id" => execution.Definition?.Id,
                "event.definition.name" => execution.Definition?.Name,
                "event.definition.revision" => execution.Definition?.Revision,
                "event.definition.contentHash" => execution.Definition?.ContentHash,
                "event.target.agentId" => execution.Target?.AgentId?.ToString("D"),
                "event.target.displayName" => execution.Target?.DisplayName,
                "event.target.hostName" => execution.Target?.HostName,
                "event.exitCode" => execution.ExitCode,
                "event.occurredAtUtc" => execution.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
                "event.startedAtUtc" => execution.StartedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
                "event.completedAtUtc" => execution.CompletedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
                _ => UnknownPath
            };
            if (ReferenceEquals(value, UnknownPath)) return Missing("event-path");
        }
        else if (alert is not null && path?.StartsWith("alert.", StringComparison.Ordinal) is true)
        {
            value = path switch
            {
                "alert.ruleName" => alert.RuleName,
                "alert.clientName" => alert.ClientName,
                "alert.resource" => alert.Resource,
                "alert.metric" => alert.Metric,
                "alert.severity" => alert.Severity,
                "alert.numericValue" => alert.NumericValue,
                "alert.serviceState" => alert.ServiceState,
                "alert.agentId" => alert.AgentId.ToString("D"),
                "alert.ruleId" => alert.RuleId.ToString("D"),
                "alert.observedAtUtc" => alert.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                _ => UnknownPath
            };
            if (ReferenceEquals(value, UnknownPath)) return Missing("event-path");
        }
        else return Missing("event-unavailable");
        return new(true, value is null ? "captured-null" : "available", JsonSerializer.SerializeToElement(value, Json));
    }

    public static string Outcome(FlowExecutionOutcome outcome) => outcome switch
    {
        FlowExecutionOutcome.Succeeded => FlowNodeCatalogue.Succeeded,
        FlowExecutionOutcome.Failed => FlowNodeCatalogue.Failed,
        FlowExecutionOutcome.TimedOut => FlowNodeCatalogue.TimedOut,
        FlowExecutionOutcome.Cancelled => FlowNodeCatalogue.Cancelled,
        FlowExecutionOutcome.Unknown => FlowNodeCatalogue.Unknown,
        FlowExecutionOutcome.Unsupported => FlowNodeCatalogue.Unsupported,
        FlowExecutionOutcome.Denied => FlowNodeCatalogue.Denied,
        _ => "unavailable"
    };

    private static FlowValueResolution Missing(string code) => new(false, code);
}

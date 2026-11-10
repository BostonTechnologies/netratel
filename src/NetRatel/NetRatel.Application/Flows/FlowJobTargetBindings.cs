using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Application.Flows;

/// <summary>Target values share the persisted Flow checkpoint, but never become script inputs or selector text.</summary>
public static class FlowJobTargetBindings
{
    public const string CheckpointKey = "$netratel.jobTarget";

    public static IEnumerable<KeyValuePair<string, FlowInputBindingDto>> Mappings(FlowNodeDto node)
    {
        foreach (var pair in node.Inputs ?? new Dictionary<string, FlowInputBindingDto>()) yield return pair;
        if (node.Kind == FlowNodeKind.RunJob && node.ExecutionAction?.TargetBinding is { } target)
            yield return new(CheckpointKey, target.Value);
    }

    public static FlowDataType DataType(JobRuntimeTargetKind kind) => FlowJobTargetBindingValues.DataType(kind);

    public static (string InputsJson, JobRuntimeTargetInput? Target) Split(FlowNodeDto node, string inputsJson)
    {
        using var document = JsonDocument.Parse(inputsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Resolved execution inputs must be an object.");
        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        JsonElement? targetValue = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name == CheckpointKey)
            {
                if (targetValue is not null) throw new ArgumentException("Duplicate target binding.");
                targetValue = property.Value.Clone();
            }
            else if (!inputs.TryAdd(property.Name, property.Value.Clone()))
                throw new ArgumentException("Duplicate execution input.");
        }
        var binding = node.ExecutionAction?.TargetBinding;
        if (binding is null)
        {
            if (targetValue is not null) throw new ArgumentException("An undeclared target binding was supplied.");
            return (JsonSerializer.Serialize(inputs), null);
        }
        if (node.Kind != FlowNodeKind.RunJob || node.ExecutionAction?.JobTarget?.Source != JobTargetSource.RuntimeInput ||
            binding.Kind != node.ExecutionAction.JobTarget.RuntimeInput?.Kind ||
            binding.Value.DataType != DataType(binding.Kind) || targetValue is null)
            throw new ArgumentException("A complete typed runtime job target is required.");
        return (JsonSerializer.Serialize(inputs), Literal(binding, targetValue.Value));
    }

    public static JobRuntimeTargetInput Literal(FlowJobTargetBindingDto binding, JsonElement value) =>
        FlowJobTargetBindingValues.TryCreate(binding, value, out var target) ? target! :
            throw new ArgumentException("A complete typed literal runtime target is required.");
}

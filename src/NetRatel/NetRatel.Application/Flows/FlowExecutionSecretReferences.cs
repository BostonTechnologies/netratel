using System.Text;
using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>
/// Carries only a native target-local reference name into canonical protected admission.
/// It never reads a secret value or adds reference inputs to a Flow checkpoint.
/// </summary>
public static class FlowExecutionSecretReferences
{
    public static void Validate(FlowNodeDto node, FlowPinnedExecutionDefinition pin) => _ = References(node, pin);

    public static string MergeInputs(FlowNodeDto node, FlowPinnedExecutionDefinition pin, string inputsJson)
    {
        var references = References(node, pin);
        if (references.Count == 0) return inputsJson;
        using var document = JsonDocument.Parse(inputsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(inputsJson) > 131072)
            throw new ArgumentException("Resolved execution inputs must be a bounded object.");
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!values.TryAdd(property.Name, property.Value.Clone()))
                throw new ArgumentException("Execution parameter names must be unique.");
        foreach (var reference in references)
            if (!values.TryAdd(reference.Key, JsonSerializer.SerializeToElement(reference.Value)))
                throw new ArgumentException("A reference input cannot be replaced by a resolved or literal value.");
        var json = JsonSerializer.Serialize(values);
        if (values.Count > 128 || Encoding.UTF8.GetByteCount(json) > 131072)
            throw new ArgumentException("Resolved execution inputs exceed their bound.");
        return json;
    }

    public static bool IsNativeReferenceName(string? reference) => reference is { Length: > 0 and <= 128 } &&
        (char.IsAsciiLetter(reference[0]) || reference[0] == '_') &&
        reference.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static IReadOnlyDictionary<string, string> References(FlowNodeDto node, FlowPinnedExecutionDefinition pin)
    {
        var bindings = (node.Inputs ?? new Dictionary<string, FlowInputBindingDto>())
            .Where(binding => binding.Value.Source == FlowInputSource.SecretReference).ToArray();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (bindings.Length == 0) return result;
        if (pin.NodeId != node.Id || pin.Kind != node.Kind ||
            node.Kind is not (FlowNodeKind.RunJob or FlowNodeKind.RunLibraryScript))
            throw new FlowExecutionPinException("secret-reference-definition-unavailable");
        var parameters = node.Kind == FlowNodeKind.RunJob && pin.Job is { } job
            ? job.Definition.Params.Select(parameter => (parameter.Name, parameter.Type)).ToArray()
            : node.Kind == FlowNodeKind.RunLibraryScript && pin.Script is { } script
                ? script.Parameters.Select(parameter => (parameter.Name, parameter.Type)).ToArray() : [];
        foreach (var binding in bindings)
        {
            var parameter = parameters.Where(parameter => parameter.Name.Equals(binding.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (parameter.Length != 1 || !parameter[0].Type.Equals("secret_reference", StringComparison.OrdinalIgnoreCase))
                throw new FlowExecutionPinException("secret-reference-parameter-unavailable");
            if (binding.Value.DataType != FlowDataType.SecretReference || binding.Value.NodeId is not null ||
                binding.Value.Literal is not null || !IsNativeReferenceName(binding.Value.Path))
                throw new FlowExecutionPinException("secret-reference-invalid");
            if (!result.TryAdd(parameter[0].Name, binding.Value.Path!))
                throw new FlowExecutionPinException("secret-reference-parameter-conflict");
        }
        return result;
    }
}

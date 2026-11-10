using System.Text.Json;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Shared.Contracts.Flows;

/// <summary>Typed literal shape validation shared by authoring, pure preview and durable invocation.</summary>
public static class FlowJobTargetBindingValues
{
    public static FlowDataType DataType(JobRuntimeTargetKind kind) => kind is
        JobRuntimeTargetKind.ClientIds or JobRuntimeTargetKind.Hostnames or JobRuntimeTargetKind.Tags
            ? FlowDataType.Json : FlowDataType.Text;

    public static bool TryCreate(FlowJobTargetBindingDto binding, JsonElement value, out JobRuntimeTargetInput? target)
    {
        target = null;
        var multiple = DataType(binding.Kind) == FlowDataType.Json;
        if (!Enum.IsDefined(binding.Kind) || binding.AllOf && binding.Kind is not (JobRuntimeTargetKind.Tag or JobRuntimeTargetKind.Tags) ||
            (multiple ? value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)
                : value.ValueKind != JsonValueKind.String)) return false;
        var values = multiple ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : [value.GetString()!];
        if (values.Length is < 1 or > 10000 || values.Any(text => string.IsNullOrWhiteSpace(text) || text.Length > 1024) ||
            binding.Kind is JobRuntimeTargetKind.ClientId or JobRuntimeTargetKind.ClientIds &&
                values.Any(text => !Guid.TryParse(text, out var id) || id == Guid.Empty)) return false;
        target = new(binding.Kind, values, binding.AllOf);
        return true;
    }
}

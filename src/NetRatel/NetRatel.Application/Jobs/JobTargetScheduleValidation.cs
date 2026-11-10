using System.Text.Json;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Application.Jobs;

/// <summary>Saved schedule activation must leave no target input to an absent interactive caller.</summary>
public static class JobTargetScheduleValidation
{
    public static void RequireComplete(JobTargetDefinition? target, string? optionsJson,
        IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        if (target is null || !ScheduleEnabled(optionsJson)) return;
        if (target.Source == JobTargetSource.RuntimeInput)
        {
            var runtime = target.RuntimeInput!;
            var values = runtime.DefaultValues;
            if (values is null || values.Count == 0 || values.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > JobNodeFilterParser.MaximumLiteralLength) ||
                runtime.Kind is JobRuntimeTargetKind.ClientId or JobRuntimeTargetKind.Hostname or JobRuntimeTargetKind.Tag && values.Count != 1 ||
                runtime.Kind is JobRuntimeTargetKind.ClientId or JobRuntimeTargetKind.ClientIds && values.Any(x => !Guid.TryParse(x, out var id) || id == Guid.Empty))
                throw new JobTargetValidationException("target_schedule_inputs_required", "An enabled runtime-target schedule requires complete typed saved target defaults.");
        }
        else if (target.Source == JobTargetSource.NodeFilter)
        {
            var expression = JobNodeFilterParser.Parse(target.Filter);
            var defaults = parameters.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var name in JobNodeFilterParser.ParameterNames(expression))
                if (!defaults.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                    throw new JobTargetValidationException("target_schedule_inputs_required", "Every parameterized target operand needs a declared saved default before enabling a schedule.");
        }
    }

    private static bool ScheduleEnabled(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            if (!document.RootElement.TryGetProperty("schedule", out var schedule) || schedule.ValueKind != JsonValueKind.Object) return false;
            return !schedule.TryGetProperty("enabled", out var enabled) || enabled.ValueKind == JsonValueKind.True ||
                enabled.ValueKind == JsonValueKind.String && bool.TryParse(enabled.GetString(), out var value) && value;
        }
        catch (JsonException) { return false; }
    }
}

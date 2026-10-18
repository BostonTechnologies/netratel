using System.Text.Json;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Security;

namespace NetRatel.Infrastructure.Services;

internal static class TaskOutputProtection
{
    internal static TaskOutputPolicy? Metadata(JobTaskActivityRecord activity) => Metadata(activity.OutputProtectionJson);

    internal static TaskOutputPolicy? Metadata(string? outputProtectionJson)
    {
        try
        {
            var policy = JsonSerializer.Deserialize<TaskOutputPolicy>(outputProtectionJson ?? "null");
            return policy is { Binding.Length: 32, SecretParameters: not null,
                SecretReferenceParameters: not null, EnvironmentReferences: not null } && policy.Binding.All(char.IsAsciiHexDigit)
                ? policy : null;
        }
        catch (JsonException) { return null; }
    }

    internal static bool IsUnclassifiedScript(JobTaskActivityRecord activity) =>
        Metadata(activity) is null && activity.TaskType is TaskKinds.ExecLibraryScript or TaskKinds.Legacy_RunLibraryScript;

    internal static string? Project(JobTaskActivityRecord activity, string? value, string? binding = null, bool envelope = true)
        => Project(activity.OutputProtectionJson, value, IsUnclassifiedScript(activity), binding, envelope);

    internal static string? Project(string? outputProtectionJson, string? value, bool classificationRequired,
        string? binding = null, bool envelope = true)
    {
        var policy = Metadata(outputProtectionJson);
        if (policy is null) return outputProtectionJson is not null || classificationRequired ? Withhold() : value;
        if (!policy.HasSecrets) return value;
        return !policy.WithholdOutput && string.Equals(policy.Binding, binding ?? TaskOutputRedactor.ReadBinding(value), StringComparison.Ordinal)
            ? value : Withhold();
        string? Withhold() => envelope ? TaskOutputRedactor.WithholdEnvelope(value) : TaskOutputRedactor.Withhold(value);
    }

    internal static bool HasEnvironmentReferences(string? json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json ?? "null") is not { Length: 0 }; }
        catch (JsonException) { return true; }
    }

    internal static string? Serialize(TaskOutputPolicy? policy) => policy is null ? null : JsonSerializer.Serialize(policy);

    internal static void Bind(JobTaskActivityRecord activity, TaskOutputPolicy? policy)
    {
        if (policy is null) return;
        var json = Serialize(policy);
        if (activity.OutputProtectionJson is null && activity.RequestId.Length > 0)
            throw new InvalidOperationException("Historical output classification cannot be replaced by a new dispatch policy.");
        if (activity.OutputProtectionJson is not null && activity.OutputProtectionJson != json)
            throw new InvalidOperationException("Task output classification cannot change after dispatch.");
        activity.OutputProtectionJson = json;
    }
}

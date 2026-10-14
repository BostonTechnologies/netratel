using System.Text.Json;
using System.Text.Json.Nodes;
using NetRatel.Application.Jobs;
using NetRatel.Shared;

namespace NetRatel.API.Services.Jobs;

/// <summary>Selected execution environment; absence in older definitions stays unknown.</summary>
public static class JobExecutionContextPolicy
{
    public static ClientEnvironment? FromJob(JobDefinitionInfo job) => FromOptions(job.OptionsJson);

    public static ClientEnvironment? FromOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("executionEnvironment", out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var number) && Enum.IsDefined((ClientEnvironment)number))
                return (ClientEnvironment)number;
        }
        catch (JsonException) { }
        return null;
    }

    public static string MergeOptionsJson(string? optionsJson, ClientEnvironment? environment)
    {
        if (environment is { } value && !Enum.IsDefined(value))
            throw new InvalidOperationException("The selected execution environment is invalid.");
        JsonObject root;
        try { root = JsonNode.Parse(optionsJson ?? "{}") as JsonObject ?? new(); }
        catch (JsonException) { root = new(); }
        if (environment is not null) root["executionEnvironment"] = (int)environment.Value;
        return root.ToJsonString();
    }
}

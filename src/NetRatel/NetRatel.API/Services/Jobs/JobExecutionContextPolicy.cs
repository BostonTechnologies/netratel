using System.Text.Json;
using System.Text.Json.Nodes;
using NetRatel.Application.Jobs;
using NetRatel.Shared;

namespace NetRatel.API.Services.Jobs;

/// <summary>Selected execution environment; absence in older definitions stays unknown.</summary>
public static class JobExecutionContextPolicy
{
    public static ClientEnvironment? FromJob(JobDefinitionInfo job) => FromOptions(job.OptionsJson);

    public static ClientEnvironment? FromOptions(string? optionsJson) => JobExecutionEnvironment.FromOptions(optionsJson);

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

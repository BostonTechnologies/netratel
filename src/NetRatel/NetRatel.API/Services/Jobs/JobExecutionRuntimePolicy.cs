using System.Text.Json;
using System.Text.Json.Nodes;
using NetRatel.Application.Jobs;
using NetRatel.API.Services.Orchestration;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

public sealed record JobExecutionRuntimePolicy(
    int ExpectedRuntimeSeconds,
    int GraceSeconds,
    int HardTimeoutSeconds)
{
    public const int DefaultExpectedRuntimeSeconds = 30 * 60;
    public const int DefaultGraceSeconds = 0;
    private const int MinimumSeconds = 1;

    public TimeSpan ExpectedRuntime => TimeSpan.FromSeconds(ExpectedRuntimeSeconds);
    public TimeSpan HardTimeout => TimeSpan.FromSeconds(HardTimeoutSeconds);

    public static JobExecutionRuntimePolicy FromJobAndRequest(JobDefinitionInfo job, RunJobRequest request)
    {
        var jobPolicy = FromJob(job);
        return FromValues(
            request.ExpectedRuntimeSeconds ?? jobPolicy.ExpectedRuntimeSeconds,
            request.GraceSeconds ?? jobPolicy.GraceSeconds,
            request.HardTimeoutSeconds);
    }

    public static JobExecutionRuntimePolicy FromJobAndIngest(JobDefinitionInfo job, NetRatelIngestRequest request)
    {
        var jobPolicy = FromJob(job);
        return FromValues(
            request.ExpectedRuntimeSeconds ?? jobPolicy.ExpectedRuntimeSeconds,
            request.GraceSeconds ?? jobPolicy.GraceSeconds,
            request.HardTimeoutSeconds);
    }

    public static JobExecutionRuntimePolicy FromJob(JobDefinitionInfo job)
    {
        if (string.IsNullOrWhiteSpace(job.OptionsJson))
        {
            return FromValues(null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(job.OptionsJson);
            var root = doc.RootElement;
            var policy = root.TryGetProperty("executionPolicy", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;

            return FromValues(
                ReadInt(policy, "expectedRuntimeSeconds"),
                ReadInt(policy, "graceSeconds"),
                ReadInt(policy, "hardTimeoutSeconds"));
        }
        catch (JsonException)
        {
            return FromValues(null, null, null);
        }
    }

    public static JobExecutionRuntimePolicy FromValues(int? expectedRuntimeSeconds, int? graceSeconds, int? hardTimeoutSeconds)
    {
        var expected = Math.Max(expectedRuntimeSeconds ?? DefaultExpectedRuntimeSeconds, MinimumSeconds);
        var grace = Math.Max(graceSeconds ?? DefaultGraceSeconds, 0);
        var hard = Math.Max(hardTimeoutSeconds ?? expected + grace, expected);
        return new JobExecutionRuntimePolicy(expected, grace, hard);
    }

    public static string MergeOptionsJson(string? optionsJson, JobExecutionRuntimePolicy policy)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(optionsJson)
                ? new JsonObject()
                : JsonNode.Parse(optionsJson)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        root["executionPolicy"] = new JsonObject
        {
            ["expectedRuntimeSeconds"] = policy.ExpectedRuntimeSeconds,
            ["graceSeconds"] = policy.GraceSeconds,
            ["hardTimeoutSeconds"] = policy.HardTimeoutSeconds
        };

        return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static int? ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node))
        {
            return null;
        }

        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var number))
        {
            return number;
        }

        if (node.ValueKind == JsonValueKind.String && int.TryParse(node.GetString(), out var textNumber))
        {
            return textNumber;
        }

        return null;
    }
}

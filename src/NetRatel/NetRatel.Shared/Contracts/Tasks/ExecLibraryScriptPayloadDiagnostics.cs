using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetRatel.Shared.Contracts.Tasks;

public static class ExecLibraryScriptPayloadDiagnostics
{
    public static string? Sanitize(string taskType, string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return payload;
        }
        if (!string.Equals(NormalizeTaskType(taskType), TaskKinds.ExecLibraryScript, StringComparison.OrdinalIgnoreCase))
            return "[omitted]";

        try
        {
            var parsed = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(payload);
            if (parsed is null || parsed.ScriptContent is null)
            {
                return "[omitted]";
            }

            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parsed.ScriptContent))).ToLowerInvariant();
            parsed.ScriptContent = "[omitted]";
            parsed.Parameters = null;
            parsed.OutputSecretValues = null;
            parsed.OutputPolicy = null;

            var safePayload = JsonSerializer.SerializeToNode(parsed)?.AsObject();
            if (safePayload is not null)
            {
                safePayload["scriptContentSha256"] = contentHash;
                return safePayload.ToJsonString();
            }

            return JsonSerializer.Serialize(parsed);
        }
        catch (JsonException)
        {
            return "[omitted]";
        }
    }

    private static string NormalizeTaskType(string? value)
    {
        if (string.Equals(value, TaskKinds.Legacy_RunLibraryScript, StringComparison.OrdinalIgnoreCase))
        {
            return TaskKinds.ExecLibraryScript;
        }

        return value ?? string.Empty;
    }
}

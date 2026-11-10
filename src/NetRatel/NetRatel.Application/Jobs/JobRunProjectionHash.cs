using System.Security.Cryptography;
using System.Text.Json;

namespace NetRatel.Application.Jobs;

/// <summary>Canonical accepted run projection hash shared by authority and verified storage migration.</summary>
public static class JobRunProjectionHash
{
    public static string Compute(JobRunDetails details)
    {
        // Keep typed scalar values: converting timestamps through JsonNode would
        // re-escape their UTC offset and change already published terminal hashes.
        static Dictionary<string, object?> Shape(object value, params string[] newEvidence)
        {
            var fields = new Dictionary<string, object?>();
            foreach (var property in value.GetType().GetProperties())
            {
                // Storage-protection evidence is not part of the accepted execution result.
                if (property.Name is nameof(JobRunInfo.InputProtectionStatus) or nameof(JobRunInfo.SubmittedDefinitionRevision)) continue;
                // Schema-one native hashes predate aggregate projections. Presentation defaults
                // (including TargetCount=1) must not rewrite an already accepted terminal receipt.
                if (value is JobRunInfo { IsAggregate: false, ParentRunId: null, TargetPlanDigest: null } &&
                    property.Name is nameof(JobRunInfo.IsAggregate) or nameof(JobRunInfo.ParentRunId) or
                        nameof(JobRunInfo.TargetPlanDigest) or nameof(JobRunInfo.DefinitionDigest) or
                        nameof(JobRunInfo.TargetSource) or nameof(JobRunInfo.ExecutionPolicy) or
                        nameof(JobRunInfo.TargetCount) or nameof(JobRunInfo.EffectiveConcurrency) or nameof(JobRunInfo.NodeCounts)) continue;
                var field = property.GetValue(value);
                if (field is null && newEvidence.Contains(property.Name)) continue;
                fields.Add(property.Name, field);
            }
            return fields;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Run = Shape(details.Run, "Environment", "TargetDisplayName", "TargetHostName", "TargetName"),
            Steps = details.Steps.OrderBy(x => x.Id),
            Activities = details.Activities.OrderBy(x => x.Id).Select(activity => Shape(activity,
                "Environment", "StartedAtUtc", "ExitCode", "TargetDisplayName", "TargetHostName", "TargetName",
                "ScriptName", "ScriptType", "CommandLabel", "ScriptContentHash"))
        });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

}

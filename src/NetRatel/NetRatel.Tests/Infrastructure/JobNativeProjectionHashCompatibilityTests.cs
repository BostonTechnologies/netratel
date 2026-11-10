using System.Security.Cryptography;
using System.Text.Json;
using NetRatel.Application.Jobs;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class JobNativeProjectionHashCompatibilityTests
{
    [Fact]
    public void Historical_native_terminal_hash_keeps_the_pre_aggregate_schema_even_with_singleton_presentation_defaults()
    {
        var at = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var run = new JobRunInfo(91, 41, 7, "synthetic-node", "synthetic-operator", JobRunState.Succeeded, 2,
            at, at, at.AddMinutes(1), null, "{}", "{}", Guid.Parse("11111111-1111-1111-1111-111111111111"),
            NetRatel.Shared.ClientEnvironment.Dev, "Synthetic node", "synthetic.example.test", "synthetic-node",
            "protected", "submission-metadata");
        // This is the published native schema from before aggregate fields existed. Keep the
        // typed scalar values and their original order; storage-protection metadata was excluded.
        var originalBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Run = new
            {
                run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy, run.Status, run.CurrentStepOrdinal,
                run.CreatedAtUtc, run.StartedAtUtc, run.CompletedAtUtc, run.Error, run.InputsJson, run.OptionsJson,
                run.AgentId, run.Environment, run.TargetDisplayName, run.TargetHostName, run.TargetName
            },
            Steps = Array.Empty<JobStepRunInfo>(),
            Activities = Array.Empty<JobTaskActivityInfo>()
        });
        var originalHash = Convert.ToHexStringLower(SHA256.HashData(originalBytes));
        Assert.Equal(originalHash, JobRunProjectionHash.Compute(new(run, [], [])));
        Assert.Equal(originalHash, JobRunProjectionHash.Compute(new(run with { TargetCount = 1, EffectiveConcurrency = 1 }, [], [])));
    }
}

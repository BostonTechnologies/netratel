using System.Security.Cryptography;
using System.Text;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class OrchestrationCallbackHashTests
{
    [Fact]
    public void HistoricalTerminalHash_RetainsTheOriginalPublishedJsonContractWhenNewEvidenceIsAbsent()
    {
        // Shipped record shape before optional execution metadata was added.
        const string legacyJson = """
            {"Run":{"Id":51,"JobId":41,"TenantId":null,"ClientIdentity":"legacy-client","StartedBy":"operator","Status":3,"CurrentStepOrdinal":1,"CreatedAtUtc":"2026-10-01T10:00:00+00:00","StartedAtUtc":null,"CompletedAtUtc":"2026-10-01T10:00:00+00:00","Error":"original failure","InputsJson":null,"OptionsJson":null,"AgentId":null},"Steps":[],"Activities":[{"Id":61,"RequestId":"legacy-request","JobRunId":51,"JobStepId":null,"ClientIdentity":"legacy-client","TenantId":null,"TaskType":"exec-library-script","Status":"Failed","Error":"original failure","CreatedAtUtc":"2026-10-01T10:00:00+00:00","CompletedAtUtc":"2026-10-01T10:00:00+00:00","AgentId":null,"ResultJson":null}]}
            """;
        var now = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        var run = new JobRunInfo(51, 41, null, "legacy-client", "operator", JobRunState.Failed, 1, now, null, now, "original failure", null, null);
        var activity = new JobTaskActivityInfo(61, "legacy-request", 51, null, "legacy-client", null, "exec-library-script", "Failed", "original failure", now, now);
        var details = new JobRunDetails(run, [], [activity]);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacyJson)));
        Assert.Equal(expected, OrchestrationCallbackProjection.ResultHash(details));
        var protectedEvidence = details with
        {
            Run = run with { InputProtectionStatus = "submitted-definition-v1", SubmittedDefinitionRevision = "synthetic-revision" }
        };
        Assert.Equal(expected, JobRunProjectionHash.Compute(protectedEvidence));
        Assert.Equal(expected, OrchestrationCallbackProjection.ResultHash(protectedEvidence));
        Assert.NotEqual(expected, JobRunProjectionHash.Compute(protectedEvidence with { Run = protectedEvidence.Run with { InputsJson = "{}" } }));
        Assert.NotEqual(expected, OrchestrationCallbackProjection.ResultHash(details with { Run = run with { TargetDisplayName = "Captured target" } }));
    }
}

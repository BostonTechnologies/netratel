using System.Security.Cryptography;
using System.Text.Json;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Operations;

namespace NetRatel.Application.Jobs;

/// <summary>Server-captured material retained atomically with whole-plan admission.</summary>
public sealed record McpReviewedJobSubmission(McpOperatorJobExecutionSnapshot Snapshot, RunJobRequest Request)
{
    public string ComputeRequestHash() => McpOperatorJobConfirmationPayload.RunStartRequestHash(
        checked((long)Snapshot.Definition.Job.Id), ReadInputs(), Request.RuntimeTarget);

    public string ComputePayloadHash() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        jobId = Snapshot.Definition.Job.Id,
        executionSnapshot = (Snapshot with { ReviewIdempotencyId = null }).ComputeDigest(),
        inputs = ReadInputs().OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        Request.RuntimeTarget
    })));

    private IReadOnlyDictionary<string, string> ReadInputs() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(Request.InputsJson ?? "{}")
        ?? throw new InvalidOperationException("Reviewed job inputs are invalid.");
}

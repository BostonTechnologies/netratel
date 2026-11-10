using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetRatel.Application.Operations;
using NetRatel.Application.Scripts;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Application.Jobs;

/// <summary>
/// Internal execution material captured from owned database records. It is
/// never accepted from an HTTP request or included in an ordinary run view.
/// </summary>
public sealed record McpOperatorJobExecutionSnapshot(
    JobDefinitionDetails Definition,
    long OwnerVersion,
    string Subject,
    string ClientId,
    string McpResource,
    string McpInstance,
    string TargetSetDigest,
    IReadOnlyList<McpOperatorJobParameter> InputSchema,
    IReadOnlyList<McpOperatorScriptExecutionSnapshot> Scripts)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JobResolvedTargetPlan? TargetPlan { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpAggregateReviewedAuthority? ReviewedAuthority { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ReviewIdempotencyId { get; init; }

    public string ComputeDigest() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));

    /// <summary>Definition/scripts only: including the plan itself would create a recursive digest.</summary>
    public string ComputeDefinitionDigest() => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Definition, Scripts })));
}

/// <summary>Protected, server-captured current-authority facts, never accepted from a public request.</summary>
public sealed record McpAggregateReviewedAuthority(McpOperatorRouteAccessRequest Route,
    string? ExternalIssuer, string? IngressCredentialId, IReadOnlyList<McpAggregateTargetGrant> Targets);

public sealed record McpAggregateTargetGrant(Guid AgentId, Guid PolicyId, long PolicyVersion,
    string TargetFactsDigest, string ConstraintsDigest)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaximumConcurrentJobTargets { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? QuotaPolicyId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? QuotaPolicyVersion { get; init; }
}

public interface IMcpAggregateExecutionAuthority
{
    Task RequireAllowedAsync(McpOperatorJobExecutionSnapshot snapshot, Guid agentId, CancellationToken cancellationToken);
}

public static class McpAggregateAdmissionKey
{
    public static long LockKey(string key) => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(
        SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string For(int tenantId, string subject, Guid idempotencyId) =>
        $"mcp:{tenantId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject)))}:{idempotencyId:N}";
}

/// <summary>One reviewed script and its parameter types from the same persisted source snapshot.</summary>
public sealed record McpOperatorScriptExecutionSnapshot(
    ScriptInfo Script,
    IReadOnlyList<ScriptParamInfo> Parameters,
    long OperatorVersion,
    string ContentHash,
    string WorkingDirectory,
    int TimeoutSeconds);

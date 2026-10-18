using System.Security.Cryptography;
using System.Text.Json;
using NetRatel.Application.Operations;
using NetRatel.Application.Scripts;

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
    public string ComputeDigest() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
}

/// <summary>One reviewed script and its parameter types from the same persisted source snapshot.</summary>
public sealed record McpOperatorScriptExecutionSnapshot(
    ScriptInfo Script,
    IReadOnlyList<ScriptParamInfo> Parameters,
    long OperatorVersion,
    string ContentHash);

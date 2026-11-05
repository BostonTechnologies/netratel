using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Application.Jobs;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.Application.Scripts;

/// <summary>Protected source material captured by native library-script admission, never an HTTP request or trace payload.</summary>
public sealed record ScriptExecutionReplaySnapshot(int TenantId, Guid AgentId, PinnedJobScriptSnapshot Script,
    ClientEnvironment Environment, string NativePayloadJson, TaskOutputPolicy OutputPolicy)
{
    public void Validate()
    {
        var source = Script.Script;
        if (TenantId <= 0 || AgentId == Guid.Empty || source.Id is 0 or > int.MaxValue || source.SourceRevision <= 0 ||
            !Enum.IsDefined(Environment) || Script.Parameters.Count > 128 ||
            Script.Parameters.Any(parameter => parameter.ScriptId != source.Id) ||
            Encoding.UTF8.GetByteCount(NativePayloadJson) > ExecutionAdmissionSubmission.MaximumPayloadBytes ||
            !string.Equals(Script.ContentHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Content))), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The canonical script replay snapshot is invalid.");
        var payload = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(NativePayloadJson)
            ?? throw new ArgumentException("The canonical script replay payload is unavailable.");
        if (payload.ScriptId != checked((int)source.Id) || payload.ScriptContent != source.Content ||
            JsonSerializer.Serialize(payload.OutputPolicy) != JsonSerializer.Serialize(OutputPolicy) || OutputPolicy.ScriptId != checked((int)source.Id) ||
            OutputPolicy.ScriptRevision != source.SourceRevision ||
            !string.Equals(OutputPolicy.ScriptContentHash, Script.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The canonical script replay source differs from its payload classification.");
    }
}

public interface IScriptExecutionReplayStore
{
    /// <summary>Stages trusted material in the native activity transaction; identical retries retain the first pin, and different material is rejected.</summary>
    Task CaptureAsync(string requestId, ScriptExecutionReplaySnapshot snapshot, CancellationToken cancellationToken = default);
    Task<ScriptExecutionReplaySnapshot?> ResolveAsync(int tenantId, string requestId, CancellationToken cancellationToken = default);
}

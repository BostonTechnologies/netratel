using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Application.Scripts;
using NetRatel.Application.Flows;
using System.Text.Json.Serialization;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Application.Jobs;

/// <summary>Server-owned identity of one intentional action attempt. Payload data does not grant authority.</summary>
public sealed record ExecutionAdmissionIdentity(
    Guid ActionKey, int TenantId, string PrincipalId, Guid FlowVersionId, Guid FlowRunId, Guid NodeId,
    int NodeAttempt, string? IntegrationCredentialId = null)
{
    public ExecutionAdmissionCausation? Causation { get; init; }

    public void Validate()
    {
        if (ActionKey == Guid.Empty || TenantId <= 0 || string.IsNullOrWhiteSpace(PrincipalId) ||
            PrincipalId.Length > 256 || FlowVersionId == Guid.Empty || FlowRunId == Guid.Empty || NodeId == Guid.Empty ||
            NodeAttempt is < 1 or > 8 || IntegrationCredentialId?.Length > 256)
            throw new ArgumentException("A complete bounded execution admission identity is required.");
        if (Causation is { } causation && (string.IsNullOrWhiteSpace(causation.RootOccurrenceId) ||
            causation.RootOccurrenceId.Length > 128 || causation.SourceExecutionId?.Length > 256 ||
            causation.RootExecutionId?.Length > 256 || causation.RootExecutionId is not null && string.IsNullOrWhiteSpace(causation.SourceExecutionId) ||
            causation.HopCount is < 0 or > 8 || causation.RecoveryAttempt is < 0 or > 3))
            throw new ArgumentException("The execution admission causation is invalid or exhausted.");
    }
}

public sealed record ExecutionAdmissionCausation(string RootOccurrenceId, Guid? ParentEventId = null,
    int HopCount = 0, int RecoveryAttempt = 0, string? SourceExecutionId = null)
{
    // Omitted for old admissions so their protected payload and semantic hash remain unchanged.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RootExecutionId { get; init; }
}

public enum ExecutionAdmissionKind { Job = 1, LibraryScript = 2 }

/// <summary>Internal pinned material; never accept this payload from an execution HTTP endpoint or return it in a run view.</summary>
public sealed record ExecutionAdmissionSubmission(ExecutionAdmissionIdentity Identity, ExecutionAdmissionKind Kind, string PayloadJson)
{
    public const int MaximumPayloadBytes = 1024 * 1024;
    public string PayloadHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PayloadJson)));

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Identity);
        Identity.Validate();
        if (!Enum.IsDefined(Kind) || string.IsNullOrEmpty(PayloadJson) || Encoding.UTF8.GetByteCount(PayloadJson) > MaximumPayloadBytes)
            throw new ArgumentException("The execution admission payload is unavailable or exceeds its bound.");
        using var document = JsonDocument.Parse(PayloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("An execution admission payload must be a server-owned object.");
    }
}

/// <summary>The canonical identity is committed before physical dispatch. Its presence means admitted/pending, never success.</summary>
public sealed record ExecutionAdmissionBinding(ExecutionAdmissionIdentity Identity, ExecutionAdmissionKind Kind,
    string ExecutionId, string PayloadJson, DateTimeOffset CreatedAtUtc)
{
    public DateTimeOffset? DispatchStartedAtUtc { get; init; }
    public DateTimeOffset? DispatchAcceptedAtUtc { get; init; }
    public DateTimeOffset? AuthorityDeniedAtUtc { get; init; }
}

public interface IExecutionAdmissionStore
{
    Task<ExecutionAdmissionBinding> ReserveAsync(ExecutionAdmissionSubmission submission, Func<string> allocateExecutionId, CancellationToken cancellationToken);
    Task<ExecutionAdmissionBinding?> FindByExecutionAsync(ExecutionAdmissionKind kind, string executionId, CancellationToken cancellationToken);
    Task RecordAuthorityDeniedAsync(ExecutionAdmissionKind kind, string executionId, CancellationToken cancellationToken);
    Task<bool> TryBeginDispatchAsync(ExecutionAdmissionBinding binding, Func<CancellationToken, Task> prepare,
        Func<CancellationToken, Task> validate, CancellationToken cancellationToken)
        => throw new NotSupportedException("Atomic keyed physical dispatch admission is unavailable.");
    Task RecordDispatchAcceptedAsync(ExecutionAdmissionBinding binding, CancellationToken cancellationToken)
        => throw new NotSupportedException("Atomic keyed physical dispatch acceptance is unavailable.");
}

/// <summary>Re-evaluates the actual configuring principal, credential and target at every keyed dispatch boundary.</summary>
public interface IKeyedExecutionAuthority
{
    Task RequireAllowedAsync(ExecutionAdmissionIdentity identity, JobDefinitionInfo pinnedDefinition, CancellationToken cancellationToken);
    Task RequireContinuationAsync(ExecutionAdmissionIdentity identity, CancellationToken cancellationToken)
        => throw new ExecutionAdmissionDeniedException("Current persisted source admission is unavailable.");
    Task RequireFirstDispatchAsync(ExecutionAdmissionIdentity identity, FlowRunLease? lease, CancellationToken cancellationToken)
        => throw new ExecutionAdmissionDeniedException("Current source admission and flow lease proof are unavailable.");
}

public sealed class ExecutionAdmissionDeniedException(string message) : InvalidOperationException(message);
public sealed class ExecutionAdmissionBudgetExhaustedException() : InvalidOperationException("The linked rerun attempt budget is exhausted.");

public sealed record PinnedJobScriptSnapshot(ScriptInfo Script, IReadOnlyList<ScriptParamInfo> Parameters, string ContentHash);

/// <summary>A neutral immutable job/script source snapshot, separate from MCP-owned identities and grants.</summary>
public sealed record PinnedJobExecutionSnapshot(JobDefinitionDetails Definition, IReadOnlyList<PinnedJobScriptSnapshot> Scripts)
{
    public string ComputeDigest() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));

    public void Validate(int tenantId)
    {
        if (Definition.Job.Id == 0 || Definition.Job.TenantId != tenantId || Definition.Job.AgentId is not { } agent || agent == Guid.Empty ||
            Definition.Steps.Count > 128 || Scripts.Count > 128 ||
            Definition.Steps.Any(step => step.JobId != Definition.Job.Id || step.Enabled && step.Type is not (JobStepKind.RunCommand or JobStepKind.LibraryScript)) ||
            Scripts.GroupBy(script => script.Script.Id).Any(group => group.Count() != 1))
            throw new ArgumentException("The pinned job definition or bound target is invalid.");
        foreach (var script in Scripts)
        {
            if (script.Script.Id == 0 || script.Script.SourceRevision <= 0 ||
                script.Parameters.Any(parameter => parameter.ScriptId != script.Script.Id) ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.Script.Content))),
                    script.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A pinned library script revision or content hash is invalid.");
        }
        if (Definition.Steps.Any(step => step.Enabled && step.Type == JobStepKind.LibraryScript &&
            (step.ScriptId is not { } scriptId || !Scripts.Any(script => script.Script.Id == scriptId))))
            throw new ArgumentException("Every pinned library step must resolve its canonical script identity.");
    }
}

public sealed record KeyedJobAdmissionRequest(ExecutionAdmissionIdentity Identity, PinnedJobExecutionSnapshot Snapshot, RunJobRequest Request)
{
    // The transport lease changes across worker recovery and is not part of pinned execution semantics.
    [JsonIgnore] public FlowRunLease? FlowLease { get; init; }
}

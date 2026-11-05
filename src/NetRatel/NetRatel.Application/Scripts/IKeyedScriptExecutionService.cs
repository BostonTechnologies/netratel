using NetRatel.Application.Jobs;
using NetRatel.Application.Flows;
using System.Text.Json.Serialization;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Scripts;

/// <summary>Server-owned source snapshot; callers cannot submit arbitrary generated code through this authority.</summary>
public sealed record KeyedScriptAdmissionRequest(ExecutionAdmissionIdentity Identity, PinnedJobScriptSnapshot Snapshot,
    Guid AgentId, IReadOnlyDictionary<string, string> Parameters, ClientEnvironment Environment = ClientEnvironment.None,
    int TimeoutSeconds = 3600, string? WorkingDirectory = null)
{
    [JsonIgnore] public FlowRunLease? FlowLease { get; init; }
}
public sealed record KeyedScriptAdmissionResult(ExecutionAdmissionBinding Binding, JobTaskActivityInfo? Activity,
    bool DispatchUncertain = false, bool AuthorityDenied = false);
public interface IKeyedScriptExecutionService
{
    Task<KeyedScriptAdmissionResult> StartKeyedAsync(KeyedScriptAdmissionRequest request,
        CancellationToken cancellationToken = default);
    Task<KeyedScriptAdmissionResult> StartReplayAsync(ExecutionAdmissionIdentity identity, FlowRunLease lease,
        FlowExecutionReplayDescriptorDto replay, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Canonical original script replay is unavailable.");
}

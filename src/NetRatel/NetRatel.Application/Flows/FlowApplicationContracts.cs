using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>Current tenant-paired identity is rechecked; never replace this with a service/global-admin principal.</summary>
public sealed record FlowExecutionAuthorityDto(string PrincipalId, string? IntegrationCredentialId = null);
public sealed record FlowEventEnvelope(int TenantId, Guid EventId, Guid OccurrenceId, Guid FlowVersionId,
    DateTimeOffset OccurredAtUtc, FlowEventDataDto Data, FlowExecutionAuthorityDto Authority);
public enum FlowIngressDisposition { Enqueued, Duplicate, Conflict, Disabled, Invalid, CapacityExceeded }
public sealed record FlowIngressResult(FlowIngressDisposition Disposition, Guid? RunId = null, string? Code = null);
public interface IFlowEventIngress
{
    Task<FlowIngressResult> EnqueueAsync(FlowEventEnvelope input, CancellationToken cancellationToken = default);
    Task<FlowRunSummaryDto?> GetOutcomeAsync(int tenantId, Guid runId, CancellationToken cancellationToken = default);
    Task<FlowRunSummaryDto?> FindOutcomeAsync(int tenantId, Guid eventId, Guid flowVersionId, CancellationToken cancellationToken = default);
}

public sealed record FlowDispatchDecision(bool Allowed, string Code);
/// <summary>#143 supplies the current occurrence/evidence/bypass/authorization gate before unstarted effects.</summary>
public interface IFlowDispatchGuard
{
    Task<FlowDispatchDecision> CanDispatchAsync(FlowEventEnvelope input, CancellationToken cancellationToken = default);
}
public interface IFlowExecutionAuthorityVerifier
{
    Task<bool> AuthorizeAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default);
}

public sealed record FlowIncidentActionDraft(int TenantId, Guid RunId, Guid ActionNodeId, Guid ConnectorId, long ConnectorRevision,
    Guid SourceInstanceId, string IdempotencyKey, FlowEventEnvelope Event, FlowIncidentFieldsDto Fields);
public sealed record FlowIncidentTargetDto(string OrganizationId, string CustomerId, string? AssignedToId, IReadOnlyList<Guid> CategoryIds);
/// <summary>Preparation resolves and pins connector mapping before this exact semantic request is persisted and sent.</summary>
public sealed record FlowIncidentActionRequest(int TenantId, Guid RunId, Guid ActionNodeId, Guid ConnectorId,
    long ConnectorRevision, Guid SourceInstanceId, string IdempotencyKey, FlowEventEnvelope Event,
    FlowIncidentFieldsDto Fields, FlowIncidentTargetDto Target, bool SupportsSafeReplay, string SemanticFingerprint);
public enum FlowIncidentPreparationStatus { Ready, Unavailable, Denied, Invalid }
public sealed record FlowIncidentPreparationResult(FlowIncidentPreparationStatus Status, FlowIncidentActionRequest? Action = null, string? Code = null);
public enum FlowIncidentActionResultKind { Succeeded, Failed, RetryableSafe, DeliveryUnknown, Unavailable }
public sealed record FlowIncidentActionResult(FlowIncidentActionResultKind Kind, string Code,
    FlowActionReceiptDto? Receipt = null, TimeSpan? RetryAfter = null);
public interface IFlowIncidentActionDispatcher
{
    Task<FlowIncidentPreparationResult> PrepareAsync(FlowIncidentActionDraft draft, CancellationToken cancellationToken = default);
    Task<FlowIncidentActionResult> DispatchAsync(FlowIncidentActionRequest action, CancellationToken cancellationToken = default);
}
public interface IFlowConnectorCatalog
{
    Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid connectorId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default);
}

public enum FlowWriteDisposition { Stored, NotFound, Conflict, Invalid, CapacityExceeded, ConnectorDenied }
public sealed record FlowDefinitionWriteResult(FlowWriteDisposition Disposition, FlowDefinitionDto? Definition = null, string? Code = null);
public sealed record FlowPublishResult(FlowWriteDisposition Disposition, FlowVersionDto? Version = null, string? Code = null);
public interface IFlowDefinitionService
{
    Task<IReadOnlyList<FlowDefinitionDto>> ListAsync(int tenantId, CancellationToken cancellationToken = default);
    Task<FlowDefinitionDto?> GetAsync(int tenantId, Guid flowId, CancellationToken cancellationToken = default);
    Task<FlowDefinitionWriteResult> CreateAsync(int tenantId, FlowCreateRequest request, string actorId, CancellationToken cancellationToken = default);
    Task<FlowDefinitionWriteResult> SaveDraftAsync(int tenantId, Guid flowId, FlowSaveDraftRequest request, string actorId, CancellationToken cancellationToken = default);
    Task<FlowDefinitionWriteResult> CloneAsync(int tenantId, Guid flowId, FlowCloneRequest request, string actorId, CancellationToken cancellationToken = default);
    Task<FlowDefinitionWriteResult> SetEnabledAsync(int tenantId, Guid flowId, FlowEnabledRequest request, string actorId, CancellationToken cancellationToken = default);
    Task<FlowPublishResult> PublishAsync(int tenantId, Guid flowId, FlowRevisionRequest request, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowVersionDto>> GetVersionsAsync(int tenantId, Guid flowId, CancellationToken cancellationToken = default);
    Task<FlowVersionDto?> GetVersionAsync(int tenantId, Guid versionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowRunSummaryDto>> GetRunsAsync(int tenantId, Guid flowId, CancellationToken cancellationToken = default);
    Task<FlowRunDetailDto?> GetRunAsync(int tenantId, Guid flowId, Guid runId, CancellationToken cancellationToken = default);
    Task<FlowRunDetailDto?> GetRunByIdAsync(int tenantId, Guid runId, CancellationToken cancellationToken = default);
}

public sealed record FlowRunLease(Guid RunId, Guid Token, Guid WorkerId, long Fence, DateTimeOffset ExpiresAtUtc,
    Guid SourceInstanceId, FlowVersionDto Version, FlowEventEnvelope Event, int Attempts);
public sealed record FlowRuntimeResult(FlowRunStatus Status, string Code);
public interface IFlowRuntimeAdapter
{
    Task<FlowRuntimeResult> ExecuteAsync(FlowRunLease lease,
        Func<FlowIncidentActionDraft, CancellationToken, Task<FlowIncidentActionResult>> executeAction,
        CancellationToken cancellationToken = default);
}

using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Application.RatelDesk;

public enum RatelDeskAuthenticationMode { PairedSystem = 2 }

// Reference only. Paired credentials remain in protected server connection storage.
public sealed record RatelDeskConnectorAuthentication(
    RatelDeskAuthenticationMode Mode, string? ManagedLinkId);

// Persisted evidence: no client secret, bearer, credential revision or mutable display name.
public sealed record RatelDeskSemanticPeer(
    RatelDeskAuthenticationMode Mode, int LocalTenantId, Guid ConnectorId, string? LinkId,
    long? LinkRevision, string? GrantHash, string ReceiverInstanceId,
    string PeerTenantId, string ApiBaseUrl, string? Issuer, string? Audience,
    string? TokenEndpoint, string? ClientId, string? DirectionId,
    Guid SourceInstanceId, Guid SourceNamespaceId, string OrganizationId,
    string CustomerId, string? AssignedToId, string[] CategoryIds);

public sealed record RatelDeskReceiverEndpoints(
    string Capabilities, string Create, string ReceiptTemplate, string TargetValidation);

// A bounded observation, never a substitute for current authority or live capability.
public sealed record RatelDeskVerifiedCapability(
    string ContractVersion, string ReceiverInstanceId, Guid SourceInstanceId,
    Guid SourceNamespaceId, RatelDeskReceiverEndpoints Endpoints,
    long MinimumReceiptRetentionSeconds, long MaximumAutomaticReplaySeconds,
    DateTimeOffset ObservedAtUtc)
{
    // Presentation observed through the authenticated receiver; never used as authority or a delivery identity.
    public string? OrganizationName { get; init; }
    public string? CustomerName { get; init; }
}

public sealed record RatelDeskReceiverPreparationV2(
    int SchemaVersion, Guid ConnectorId, long ConnectorRevision,
    RatelDeskSemanticPeer Peer, RatelDeskVerifiedCapability Capability,
    string ReceiverIdempotencyKey, string KeyAlgorithm,
    string ExactCreateBodyJson, string ReceiverFingerprint,
    DateTimeOffset OriginalActionCreatedAtUtc, DateTimeOffset AutomaticReplayUntilUtc,
    string EvidenceFingerprint);

// Existing IncidentDto fields may be additive; retain the complete accepted body privately.
public sealed record RatelDeskVerifiedReceipt(
    string ContractVersion, string ReceiverInstanceId, Guid SourceNamespaceId,
    Guid SourceInstanceId, string Key, string Fingerprint, string Outcome,
    string IncidentId, string TrackingId, string OrganizationId, string CustomerId,
    DateTimeOffset CommittedAtUtc, string Location, string ExactAcceptedBodyJson);

public sealed record RatelDeskDispatchEvidence(
    RatelDeskReceiverPreparationV2 Prepared, bool MayHaveCommitted,
    int Attempts, DateTimeOffset? FirstPostAttemptAtUtc, bool FinalReconciliationPending = false);

public enum RatelDeskReceiverObservationKind
{
    Committed = 1, Missing = 2, Gone = 3, AuthenticationRejected = 4,
    PayloadRejected = 5, FingerprintConflict = 6, RateLimited = 7,
    Unavailable = 8, TransientReadFailure = 9, PossibleCommit = 10
}
public sealed record RatelDeskReceiverObservation(
    RatelDeskReceiverObservationKind Kind, string Code,
    RatelDeskVerifiedReceipt? Receipt = null, TimeSpan? RetryAfter = null);

// Scope is one of the frozen incident create/receipt/target scopes.
// The implementation must re-resolve current credentials for THIS target.
public interface IRatelDeskOutboundBindingResolver
{
    Task<RatelDeskSemanticPeer> CaptureAsync(RatelDeskConnectorState connector,
        RatelDeskConnectorAuthentication authentication, Guid flowSourceInstanceId,
        CancellationToken cancellationToken);
    Task<string> GetBearerAsync(RatelDeskConnectorState currentConnector,
        RatelDeskSemanticPeer capturedPeer, string requiredScope,
        CancellationToken cancellationToken);
}

public interface IRatelDeskReceiverTransport
{
    Task<RatelDeskVerifiedCapability> CapabilitiesAsync(RatelDeskSemanticPeer peer,
        string bearer, CancellationToken cancellationToken);
    Task ValidateTargetsAsync(RatelDeskSemanticPeer peer,
        RatelDeskVerifiedCapability capability, string bearer,
        CancellationToken cancellationToken);
    Task<RatelDeskReceiverObservation> LookupAsync(RatelDeskReceiverPreparationV2 prepared,
        string bearer, CancellationToken cancellationToken);
    Task<RatelDeskReceiverObservation> CreateAsync(RatelDeskReceiverPreparationV2 prepared,
        string bearer, CancellationToken cancellationToken);
}

public interface IFlowSourceIdentityResolver
{
    // Creates only the existing FlowRuntimeIdentity singleton when absent.
    Task<Guid> EnsureAsync(CancellationToken cancellationToken);
}

// Kept separate from the immutable V1 event/draft/prepared records and their hashes.
// Every mutation has exactly the existing CurrentLeaseAsync transaction/fence checks.
public interface IFlowReceiverEvidenceStore
{
    Task<DateTimeOffset?> GetOriginalCreatedAtAsync(FlowRunLease lease, CancellationToken cancellationToken);
    Task<RatelDeskDispatchEvidence?> ReadAsync(FlowRunLease lease, Guid nodeId,
        CancellationToken cancellationToken);
    Task<bool> SavePreparedAsync(FlowRunLease lease, Guid nodeId,
        RatelDeskReceiverPreparationV2 preparation, CancellationToken cancellationToken);
    Task<bool> SavePreparedActionAsync(FlowRunLease lease, Guid nodeId,
        FlowIncidentActionRequest action, RatelDeskReceiverPreparationV2 preparation, CancellationToken cancellationToken);
    Task<bool> SchedulePreparationRetryAsync(FlowRunLease lease, Guid nodeId,
        TimeSpan? retryAfter, CancellationToken cancellationToken);
    Task<bool> MarkPostAttemptAsync(FlowRunLease lease, Guid nodeId,
        CancellationToken cancellationToken);
    Task<bool> SaveReceiptAsync(FlowRunLease lease, Guid nodeId,
        RatelDeskVerifiedReceipt receipt, CancellationToken cancellationToken);
}

// Used by FlowRunProcessor when the selected connector supports the actual receiver.
// OriginalCreatedAtUtc comes from the current locked durable run, never a browser/draft clock.
public sealed record FlowReceiverPreparationResult(FlowIncidentPreparationStatus Status,
    FlowIncidentActionRequest? Action = null, RatelDeskReceiverPreparationV2? Evidence = null,
    string? Code = null, TimeSpan? RetryAfter = null);
public interface IFlowReceiverDispatcher
{
    Task<FlowReceiverPreparationResult> PrepareReceiverAsync(FlowIncidentActionDraft draft,
        DateTimeOffset originalCreatedAtUtc, CancellationToken cancellationToken);
    Task<RatelDeskReceiverObservation> DispatchReceiverAsync(FlowRunLease lease, Guid nodeId,
        RatelDeskDispatchEvidence evidence, Func<CancellationToken, Task<FlowDispatchDecision>> recheckFlowGuard,
        CancellationToken cancellationToken);
}

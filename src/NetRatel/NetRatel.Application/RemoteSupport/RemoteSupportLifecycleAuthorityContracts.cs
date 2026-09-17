using System.Threading.Channels;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Application.RemoteSupport;

/// <summary>
/// The only application boundary permitted to read or write the V2 lifecycle
/// projection. Implementations are invoked exclusively by the session actors.
/// </summary>
public interface IRemoteSupportLifecycleStore
{
    Task<RemoteSupportLifecycleOpenResult> OpenAsync(
        RemoteSupportOpenSessionCommand command,
        Guid remoteSupportSessionId,
        CancellationToken cancellationToken);

    Task<RemoteSupportSessionSnapshot?> LoadAsync(
        RemoteSupportSessionKey session,
        CancellationToken cancellationToken);

    Task<RemoteSupportLifecycleTransitionResult> TransitionAsync(
        RemoteSupportLifecycleTransition transition,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteSupportAuditEvent>> ReadAuditAsync(
        RemoteSupportSessionKey session,
        long afterAuditSequence,
        CancellationToken cancellationToken);
}

public sealed record RemoteSupportLifecycleOpenResult(
    RemoteSupportSessionSnapshot Snapshot,
    RemoteSupportAuditEvent Audit,
    bool IsDuplicate);

public sealed record RemoteSupportLifecycleTransition(
    RemoteSupportSessionKey Session,
    long? ExpectedLifecycleRevision,
    string NextState,
    string EventType,
    string ActorKind,
    string ActorId,
    Guid RequestId,
    string Outcome,
    string? FailureCode,
    DateTimeOffset OccurredAtUtc);

public enum RemoteSupportLifecycleTransitionDisposition
{
    Applied = 0,
    Duplicate = 1,
    StaleRevision = 2,
    Missing = 3,
    Rejected = 4
}

public sealed record RemoteSupportLifecycleTransitionResult(
    RemoteSupportLifecycleTransitionDisposition Disposition,
    RemoteSupportSessionSnapshot? Snapshot,
    RemoteSupportAuditEvent? Audit);

public sealed record RemoteSupportSessionResume(
    RemoteSupportSessionSnapshot Snapshot,
    IReadOnlyList<RemoteSupportAuditEvent> AuditEvents);

public interface IRemoteSupportLifecycleRouter
{
    Task<RemoteSupportSessionSnapshot> OpenAsync(
        RemoteSupportOpenSessionCommand command,
        CancellationToken cancellationToken);

    Task<RemoteSupportSessionSnapshot?> GetAsync(
        RemoteSupportSessionKey session,
        RemoteSupportOperatorBinding operatorBinding,
        CancellationToken cancellationToken);

    Task<RemoteSupportLifecycleTransitionResult> ControlAsync(
        RemoteSupportControlCommand command,
        CancellationToken cancellationToken);

    Task<RemoteSupportSessionResume?> ResumeAsync(
        RemoteSupportResumeRequest request,
        CancellationToken cancellationToken);

    Task<RemoteSupportLifecycleTransitionResult> AdvanceAsync(
        AdvanceRemoteSupportSessionLifecycle command,
        CancellationToken cancellationToken);

    /// <summary>Registers a prepared exact target with actor-owned ephemeral media readiness.</summary>
    Task<RemoteSupportLifecycleTransitionResult> PrepareMediaAsync(
        PrepareRemoteSupportMedia command,
        CancellationToken cancellationToken) =>
        Task.FromResult(new RemoteSupportLifecycleTransitionResult(RemoteSupportLifecycleTransitionDisposition.Rejected, null, null));

    /// <summary>Validates and routes one transient, typed V2 negotiation envelope.</summary>
    Task<RemoteSupportNegotiationIngressResult> NegotiateAsync(
        RemoteSupportNegotiationIngress command,
        CancellationToken cancellationToken) =>
        Task.FromResult(RemoteSupportNegotiationIngressResult.Rejected("remote_support_media_not_supported", 0));

    /// <summary>
    /// Creates an ephemeral browser edge on the local API replica. The
    /// subscription contains durable lifecycle status only, never SDP, ICE,
    /// TURN credentials, media, or actor references.
    /// </summary>
    Task<RemoteSupportBrowserEdgeSubscription?> SubscribeAsync(
        RemoteSupportSessionKey session,
        RemoteSupportOperatorBinding operatorBinding,
        long afterAuditSequence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a local, bounded browser-only negotiation edge. The reader is
    /// intentionally transient and is never used for browser resume.
    /// </summary>
    Task<RemoteSupportBrowserNegotiationSubscription?> SubscribeNegotiationAsync(
        RemoteSupportSessionKey session,
        RemoteSupportOperatorBinding operatorBinding,
        CancellationToken cancellationToken) =>
        Task.FromResult<RemoteSupportBrowserNegotiationSubscription?>(null);
}

public sealed record RemoteSupportBrowserLifecycleEvent(
    long AuditSequence,
    RemoteSupportSessionSnapshot Snapshot,
    string EventType);

public sealed class RemoteSupportBrowserEdgeSubscription(
    ChannelReader<RemoteSupportBrowserLifecycleEvent> reader,
    Func<ValueTask> unsubscribe) : IAsyncDisposable
{
    public ChannelReader<RemoteSupportBrowserLifecycleEvent> Reader { get; } = reader;

    public ValueTask DisposeAsync() => unsubscribe();
}

/// <summary>
/// A typed lifecycle envelope for trusted agent/workflow ingress. It contains
/// status metadata only: SDP, ICE, media, and credentials are never accepted.
/// </summary>
public sealed record AdvanceRemoteSupportSessionLifecycle(
    int ContractVersion,
    RemoteSupportSessionKey Session,
    Guid RequestId,
    string NextState,
    long? ExpectedLifecycleRevision,
    DateTimeOffset OccurredAtUtc);

/// <summary>Actor command joining an RS2-2 exact prepared target to one V2 lifecycle.</summary>
public sealed record PrepareRemoteSupportMedia(
    RemoteSupportSessionKey Session,
    RemoteSupportOperatorBinding Operator,
    RemoteSupportPreparedTargetResult PreparedTarget,
    long? ExpectedLifecycleRevision,
    Guid RequestId,
    DateTimeOffset RequestedAtUtc);

/// <summary>Actor ingress for browser or currently-fenced agent typed signalling.</summary>
public sealed record RemoteSupportNegotiationIngress(
    RemoteSupportV2NegotiationEnvelope Envelope,
    RemoteSupportOperatorBinding? BrowserOperator = null,
    Guid? AgentEdgeRouteId = null,
    long? AgentRouteGeneration = null);

public sealed record RemoteSupportNegotiationIngressResult(bool Accepted, string? FailureCode, long Generation)
{
    public static RemoteSupportNegotiationIngressResult Rejected(string code, long generation) => new(false, code, generation);
    public static RemoteSupportNegotiationIngressResult AcceptedResult(long generation) => new(true, null, generation);
}

public sealed class RemoteSupportBrowserNegotiationSubscription(
    ChannelReader<RemoteSupportV2NegotiationEnvelope> reader,
    Func<ValueTask> unsubscribe) : IAsyncDisposable
{
    public ChannelReader<RemoteSupportV2NegotiationEnvelope> Reader { get; } = reader;

    public ValueTask DisposeAsync() => unsubscribe();
}

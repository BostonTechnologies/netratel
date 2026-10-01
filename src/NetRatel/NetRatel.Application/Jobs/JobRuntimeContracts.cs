using NetRatel.Application.Commands;

namespace NetRatel.Application.Jobs;

public enum JobObservationKind
{
    // Numeric values map to the existing durable smallint ledger contract.
    // Preserve these ordinals when changing the CLR names.
    Run = 0,
    Step = 1
}

public enum JobMessageDisposition
{
    Accepted = 0,
    Duplicate = 1,
    StaleEvent = 2,
    InvalidTransition = 3,
    IdentityMismatch = 4,
    PersistenceUnavailable = 5
}

public enum JobCommandCorrelationStatus
{
    NotProvided = 0,
    Matched = 1,
    Missing = 2
}

public interface IJobObservation
{
    long SourceEventId { get; }

    ulong JobRunId { get; }

    ulong JobId { get; }

    int? TenantId { get; }

    string ClientIdentity { get; }

    DateTimeOffset Timestamp { get; }

    string SourceSystem { get; }

    // Historical provenance retained by the durable observation ledger. It
    // does not select the current runtime implementation.
    bool IsAuthoritative { get; }

    JobObservationKind Kind { get; }
}

public sealed record JobRunObservation(
    long SourceEventId,
    ulong JobRunId,
    ulong JobId,
    int? TenantId,
    string ClientIdentity,
    string StartedBy,
    JobRunState Status,
    int CurrentStepOrdinal,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset Timestamp,
    // This default participates in the durable (SourceSystem, SourceEventId)
    // identity. Keep the deployed token unchanged for replay/idempotency.
    string SourceSystem = "akka-job-shadow-event",
    bool IsAuthoritative = false) : IJobObservation
{
    public JobObservationKind Kind => JobObservationKind.Run;
}

public sealed record JobStepObservation(
    long SourceEventId,
    ulong JobRunId,
    ulong JobId,
    int? TenantId,
    string ClientIdentity,
    ulong JobStepRunId,
    ulong? JobStepId,
    JobStepRunState Status,
    int Ordinal,
    string? TaskRequestId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset Timestamp,
    // This default participates in the durable (SourceSystem, SourceEventId)
    // identity. Keep the deployed token unchanged for replay/idempotency.
    string SourceSystem = "akka-job-shadow-event",
    bool IsAuthoritative = false) : IJobObservation
{
    public JobObservationKind Kind => JobObservationKind.Step;
}

public sealed record RecordJobObservation(IJobObservation Observation);

public sealed record GetJobRunProjection(ulong JobRunId);

public sealed record ProbeJobRuntime;

public sealed record JobMessageResult(
    ulong JobRunId,
    JobMessageDisposition Disposition,
    JobRunState? CurrentStatus,
    long LastAcceptedSourceEventId,
    JobCommandCorrelationStatus CommandCorrelationStatus,
    CommandLifecycleStatus? CorrelatedCommandStatus);

public sealed record JobHistoryEntry(
    long SourceEventId,
    JobObservationKind Kind,
    JobRunState? RunStatus,
    JobStepRunState? StepStatus,
    ulong? JobStepRunId,
    DateTimeOffset Timestamp);

public sealed record JobStepView(
    ulong JobStepRunId,
    ulong? JobStepId,
    int Ordinal,
    JobStepRunState Status,
    string? TaskRequestId,
    JobCommandCorrelationStatus CommandCorrelationStatus,
    CommandLifecycleStatus? CorrelatedCommandStatus,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long LastAcceptedSourceEventId);

public sealed record JobRunView(
    ulong JobRunId,
    ulong? JobId,
    int? TenantId,
    string? ClientIdentity,
    string? StartedBy,
    JobRunState? Status,
    int CurrentStepOrdinal,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long LastAcceptedSourceEventId,
    IReadOnlyList<JobStepView> Steps,
    IReadOnlyList<JobHistoryEntry> RecentHistory,
    string Source,
    bool IsAuthoritative);

public sealed record JobRuntimeStatus(
    int ActiveJobs,
    ulong CompletedJobs,
    ulong FailedJobs,
    int ActiveJobSteps,
    ulong AcceptedEvents,
    ulong InvalidTransitions,
    ulong DuplicateEvents,
    ulong StaleEvents,
    ulong MissingCommandCorrelations,
    DateTimeOffset StartedAtUtc,
    string Mode,
    string Authority);

public enum JobObservationWriteDisposition
{
    Stored = 0,
    Duplicate = 1
}

public sealed record JobObservationWriteResult(
    ulong JobRunId,
    JobObservationWriteDisposition Disposition,
    JobCommandCorrelationStatus CommandCorrelationStatus,
    CommandLifecycleStatus? CorrelatedCommandStatus);

public sealed record PersistedJobObservation(
    IJobObservation Observation,
    JobCommandCorrelationStatus CommandCorrelationStatus,
    CommandLifecycleStatus? CorrelatedCommandStatus);

public sealed record JobObservationDiagnostics(
    long ObservationCount,
    ulong ReplayCount,
    ulong DuplicateDetectionCount,
    ulong MissingCommandCorrelationCount,
    ulong RecoverySuccessCount,
    DateTimeOffset? LastPersistedAtUtc,
    DateTimeOffset? LastReplayAtUtc,
    string Mode,
    string Authority);

/// <summary>
/// Stores safe, append-only job observations. Implementations do not schedule
/// jobs or dispatch commands; they preserve the durable lifecycle history.
/// </summary>
public interface IJobObservationStore
{
    Task<JobObservationWriteResult> RecordAsync(
        IJobObservation observation,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PersistedJobObservation>> ReplayAsync(
        ulong jobRunId,
        CancellationToken cancellationToken);

    Task<JobObservationDiagnostics> GetDiagnosticsAsync(
        CancellationToken cancellationToken);

    void RecordRecoverySucceeded();
}

public interface IJobRuntimeRouter
{
    Task<JobMessageResult> RecordAsync(
        RecordJobObservation message,
        CancellationToken cancellationToken);

    Task<JobRunView> GetStateAsync(
        ulong jobRunId,
        CancellationToken cancellationToken);

    Task<JobRuntimeStatus> ProbeAsync(CancellationToken cancellationToken);
}

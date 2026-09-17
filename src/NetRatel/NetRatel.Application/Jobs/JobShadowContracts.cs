using NetRatel.Application.Commands;

namespace NetRatel.Application.Jobs;

public enum JobShadowObservationKind
{
    Run = 0,
    Step = 1
}

public enum JobShadowMessageDisposition
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

public interface IJobShadowObservation
{
    long SourceEventId { get; }

    ulong JobRunId { get; }

    ulong JobId { get; }

    int? TenantId { get; }

    string ClientIdentity { get; }

    DateTimeOffset Timestamp { get; }

    string SourceSystem { get; }

    bool IsAuthoritative { get; }

    JobShadowObservationKind Kind { get; }
}

public sealed record JobRunShadowObservation(
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
    string SourceSystem = "akka-job-shadow-event",
    bool IsAuthoritative = false) : IJobShadowObservation
{
    public JobShadowObservationKind Kind => JobShadowObservationKind.Run;
}

public sealed record JobStepShadowObservation(
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
    string SourceSystem = "akka-job-shadow-event",
    bool IsAuthoritative = false) : IJobShadowObservation
{
    public JobShadowObservationKind Kind => JobShadowObservationKind.Step;
}

public sealed record RecordJobShadowObservation(IJobShadowObservation Observation);

public sealed record GetJobShadowState(ulong JobRunId);

public sealed record ProbeJobShadowRoute;

public sealed record JobShadowMessageResult(
    ulong JobRunId,
    JobShadowMessageDisposition Disposition,
    JobRunState? CurrentStatus,
    long LastAcceptedSourceEventId,
    JobCommandCorrelationStatus CommandCorrelationStatus,
    CommandLifecycleStatus? CorrelatedCommandStatus);

public sealed record JobShadowHistoryEntry(
    long SourceEventId,
    JobShadowObservationKind Kind,
    JobRunState? RunStatus,
    JobStepRunState? StepStatus,
    ulong? JobStepRunId,
    DateTimeOffset Timestamp);

public sealed record JobStepShadowState(
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

public sealed record JobRunShadowState(
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
    IReadOnlyList<JobStepShadowState> Steps,
    IReadOnlyList<JobShadowHistoryEntry> RecentHistory,
    string Source,
    bool IsAuthoritative);

public sealed record JobShadowRouteStatus(
    int ActiveShadowJobs,
    ulong CompletedShadowJobs,
    ulong FailedShadowJobs,
    int ActiveJobSteps,
    ulong AcceptedEvents,
    ulong InvalidTransitions,
    ulong DuplicateEvents,
    ulong StaleEvents,
    ulong MissingCommandCorrelations,
    DateTimeOffset StartedAtUtc,
    string Mode,
    string Authority);

public enum JobShadowPersistenceWriteDisposition
{
    Stored = 0,
    Duplicate = 1
}

public sealed record JobShadowPersistenceWriteResult(
    ulong JobRunId,
    JobShadowPersistenceWriteDisposition Disposition,
    JobCommandCorrelationStatus CommandCorrelationStatus,
    CommandLifecycleStatus? CorrelatedCommandStatus);

public sealed record PersistedJobShadowObservation(
    IJobShadowObservation Observation,
    JobCommandCorrelationStatus CommandCorrelationStatus,
    CommandLifecycleStatus? CorrelatedCommandStatus);

public sealed record JobShadowPersistenceDiagnostics(
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
/// Stores safe, append-only job shadow observations. Implementations must not
/// schedule jobs, dispatch commands, or mutate the production job read model.
/// </summary>
public interface IJobShadowPersistenceStore
{
    Task<JobShadowPersistenceWriteResult> RecordAsync(
        IJobShadowObservation observation,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PersistedJobShadowObservation>> ReplayAsync(
        ulong jobRunId,
        CancellationToken cancellationToken);

    Task<JobShadowPersistenceDiagnostics> GetDiagnosticsAsync(
        CancellationToken cancellationToken);

    void RecordRecoverySucceeded();
}

public interface IJobShadowRouter
{
    Task<JobShadowMessageResult> RecordAsync(
        RecordJobShadowObservation message,
        CancellationToken cancellationToken);

    Task<JobRunShadowState> GetStateAsync(
        ulong jobRunId,
        CancellationToken cancellationToken);

    Task<JobShadowRouteStatus> ProbeAsync(CancellationToken cancellationToken);
}

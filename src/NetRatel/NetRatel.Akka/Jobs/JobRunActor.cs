using Akka.Actor;
using NetRatel.Application.Jobs;

namespace NetRatel.Akka.Jobs;

/// <summary>
/// Reconstructs and validates one job run from the append-only shadow ledger.
/// It never schedules work, dispatches commands, or mutates production job state.
/// </summary>
public sealed class JobRunActor : ReceiveActor
{
    private const int HistoryLimit = 32;
    private readonly ulong _jobRunId;
    private readonly IJobShadowPersistenceStore _persistenceStore;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<ulong, MutableStepState> _steps = [];
    private readonly List<JobShadowHistoryEntry> _recentHistory = new(HistoryLimit);
    private IJobShadowObservation? _lastObservation;
    private ulong? _jobId;
    private int? _tenantId;
    private bool _tenantBound;
    private bool _isAuthoritative;
    private string? _clientIdentity;
    private string? _startedBy;
    private JobRunState? _status;
    private int _currentStepOrdinal;
    private DateTimeOffset? _createdAtUtc;
    private DateTimeOffset? _startedAtUtc;
    private DateTimeOffset? _completedAtUtc;
    private long _lastAcceptedSourceEventId;
    private bool _recovered;

    public JobRunActor(ulong jobRunId, IJobShadowPersistenceStore persistenceStore)
    {
        if (jobRunId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(jobRunId));
        }

        _jobRunId = jobRunId;
        _persistenceStore = persistenceStore ?? throw new ArgumentNullException(nameof(persistenceStore));

        ReceiveAsync<RoutedJobShadowRecord>(async message =>
        {
            // Akka.ActorContext is scoped to the synchronous receive turn. Capture
            // the parent before awaiting persistence/recovery work, otherwise the
            // reply is lost and the caller's Ask times out at runtime.
            var parent = Context.Parent;
            try
            {
                var recoveredFromHistory = await EnsureRecoveredAsync().ConfigureAwait(false);
                var previousStatus = _status;
                var previousActiveSteps = CountActiveSteps();
                var result = await RecordAsync(message.Message.Observation).ConfigureAwait(false);
                parent.Tell(new RoutedJobShadowResult(
                    result,
                    message.ReplyTo,
                    previousStatus,
                    _status,
                    previousActiveSteps,
                    CountActiveSteps(),
                    recoveredFromHistory));
            }
            catch (Exception) when (!_stopping.IsCancellationRequested)
            {
                // A failed initial replay must be observable by the caller. Letting
                // ReceiveAsync fault loses the Ask reply and turns a storage outage
                // into an unbounded gateway timeout.
                parent.Tell(new RoutedJobShadowResult(
                    CreateResult(
                        JobShadowMessageDisposition.PersistenceUnavailable,
                        JobCommandCorrelationStatus.NotProvided,
                        null),
                    message.ReplyTo,
                    _status,
                    _status,
                    CountActiveSteps(),
                    CountActiveSteps(),
                    RecoveredFromHistory: false));
            }
        });
        ReceiveAsync<GetJobShadowState>(async message =>
        {
            var replyTo = Sender;
            try
            {
                await EnsureRecoveredAsync().ConfigureAwait(false);
                replyTo.Tell(message.JobRunId == _jobRunId ? CreateState() : EmptyState(message.JobRunId));
            }
            catch (Exception) when (!_stopping.IsCancellationRequested)
            {
                replyTo.Tell(EmptyState(message.JobRunId, "akka-shadow-persistence-unavailable"));
            }
        });
    }

    public static Props Props(ulong jobRunId, IJobShadowPersistenceStore persistenceStore) =>
        global::Akka.Actor.Props.Create(() => new JobRunActor(jobRunId, persistenceStore));

    protected override void PostStop()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        base.PostStop();
    }

    internal static JobRunShadowState EmptyState(
        ulong jobRunId,
        string source = "akka-shadow") =>
        new(
            jobRunId,
            null,
            null,
            null,
            null,
            null,
            0,
            null,
            null,
            null,
            0,
            [],
            [],
            source,
            IsAuthoritative: false);

    private async Task<JobShadowMessageResult> RecordAsync(IJobShadowObservation observation)
    {
        try
        {
            var disposition = GetDisposition(observation);
            var correlationStatus = JobCommandCorrelationStatus.NotProvided;
            NetRatel.Application.Commands.CommandLifecycleStatus? commandStatus = null;

            if (disposition is JobShadowMessageDisposition.Accepted or JobShadowMessageDisposition.Duplicate)
            {
                var persistence = await _persistenceStore.RecordAsync(observation, _stopping.Token)
                    .ConfigureAwait(false);
                correlationStatus = persistence.CommandCorrelationStatus;
                commandStatus = persistence.CorrelatedCommandStatus;
                if (persistence.Disposition == JobShadowPersistenceWriteDisposition.Duplicate &&
                    disposition == JobShadowMessageDisposition.Accepted)
                {
                    ResetState();
                    _recovered = false;
                    await EnsureRecoveredAsync().ConfigureAwait(false);
                    disposition = JobShadowMessageDisposition.Duplicate;
                }
            }

            if (disposition == JobShadowMessageDisposition.Accepted)
            {
                Apply(observation, correlationStatus, commandStatus);
            }

            return CreateResult(disposition, correlationStatus, commandStatus);
        }
        catch (Exception) when (!_stopping.IsCancellationRequested)
        {
            return CreateResult(
                JobShadowMessageDisposition.PersistenceUnavailable,
                JobCommandCorrelationStatus.NotProvided,
                null);
        }
    }

    private JobShadowMessageDisposition GetDisposition(IJobShadowObservation observation)
    {
        if (observation.JobRunId != _jobRunId ||
            (_lastObservation is not null && observation.IsAuthoritative != _isAuthoritative) ||
            (_jobId.HasValue && observation.JobId != _jobId.Value) ||
            (_tenantBound && observation.TenantId != _tenantId) ||
            (_clientIdentity is not null &&
             !string.Equals(observation.ClientIdentity, _clientIdentity, StringComparison.Ordinal)))
        {
            return JobShadowMessageDisposition.IdentityMismatch;
        }

        if (_lastObservation is not null && observation.SourceEventId == _lastAcceptedSourceEventId)
        {
            return Equals(observation, _lastObservation)
                ? JobShadowMessageDisposition.Duplicate
                : JobShadowMessageDisposition.IdentityMismatch;
        }

        if (_lastObservation is not null && observation.SourceEventId < _lastAcceptedSourceEventId)
        {
            return JobShadowMessageDisposition.StaleEvent;
        }

        return observation switch
        {
            JobRunShadowObservation run => IsValidRunObservation(run)
                ? JobShadowMessageDisposition.Accepted
                : JobShadowMessageDisposition.InvalidTransition,
            JobStepShadowObservation step => IsValidStepObservation(step)
                ? JobShadowMessageDisposition.Accepted
                : JobShadowMessageDisposition.InvalidTransition,
            _ => JobShadowMessageDisposition.IdentityMismatch
        };
    }

    private bool IsValidRunObservation(JobRunShadowObservation run)
    {
        if (run.CurrentStepOrdinal < 0 || run.CurrentStepOrdinal < _currentStepOrdinal)
        {
            return false;
        }

        if (!_status.HasValue)
        {
            return true;
        }

        if (IsTerminal(run.Status) && CountActiveSteps() > 0)
        {
            return false;
        }

        return (_status.Value, run.Status) switch
        {
            (JobRunState.Pending, JobRunState.Pending) => true,
            (JobRunState.Pending, JobRunState.Running) => true,
            (JobRunState.Pending, JobRunState.Failed) => true,
            (JobRunState.Pending, JobRunState.Cancelled) => true,
            (JobRunState.Pending, JobRunState.TimedOut) => true,
            (JobRunState.Running, JobRunState.Running) => true,
            (JobRunState.Running, JobRunState.Succeeded) => true,
            (JobRunState.Running, JobRunState.Failed) => true,
            (JobRunState.Running, JobRunState.Cancelled) => true,
            (JobRunState.Running, JobRunState.TimedOut) => true,
            _ => false
        };
    }

    private bool IsValidStepObservation(JobStepShadowObservation step)
    {
        if (step.JobStepRunId == 0 || step.Ordinal < 0 || IsTerminal(_status))
        {
            return false;
        }

        if (!_steps.TryGetValue(step.JobStepRunId, out var current))
        {
            return step.Status != JobStepRunState.Running || CountActiveSteps() == 0;
        }

        if (current.Ordinal != step.Ordinal ||
            (current.JobStepId.HasValue && step.JobStepId.HasValue &&
             current.JobStepId.Value != step.JobStepId.Value) ||
            (!string.IsNullOrWhiteSpace(current.TaskRequestId) &&
             !string.IsNullOrWhiteSpace(step.TaskRequestId) &&
             !string.Equals(current.TaskRequestId, step.TaskRequestId, StringComparison.Ordinal)))
        {
            return false;
        }

        if (step.Status == JobStepRunState.Running &&
            current.Status != JobStepRunState.Running &&
            _steps.Values.Any(item => item.JobStepRunId != step.JobStepRunId &&
                                      item.Status == JobStepRunState.Running))
        {
            return false;
        }

        return (current.Status, step.Status) switch
        {
            (JobStepRunState.Pending, JobStepRunState.Pending) => true,
            (JobStepRunState.Pending, JobStepRunState.Running) => true,
            (JobStepRunState.Pending, JobStepRunState.Skipped) => true,
            (JobStepRunState.Running, JobStepRunState.Running) => true,
            (JobStepRunState.Running, JobStepRunState.Succeeded) => true,
            (JobStepRunState.Running, JobStepRunState.Failed) => true,
            _ => false
        };
    }

    private async Task<bool> EnsureRecoveredAsync()
    {
        if (_recovered)
        {
            return false;
        }

        var replay = await _persistenceStore.ReplayAsync(_jobRunId, _stopping.Token)
            .ConfigureAwait(false);
        ResetState();
        try
        {
            foreach (var persisted in replay)
            {
                if (GetDisposition(persisted.Observation) != JobShadowMessageDisposition.Accepted)
                {
                    throw new InvalidOperationException(
                        $"Durable job shadow history for run '{_jobRunId}' contains an invalid transition.");
                }

                Apply(
                    persisted.Observation,
                    persisted.CommandCorrelationStatus,
                    persisted.CorrelatedCommandStatus);
            }
        }
        catch
        {
            ResetState();
            throw;
        }

        _recovered = true;
        _persistenceStore.RecordRecoverySucceeded();
        return replay.Count > 0;
    }

    private void Apply(
        IJobShadowObservation observation,
        JobCommandCorrelationStatus correlationStatus,
        NetRatel.Application.Commands.CommandLifecycleStatus? commandStatus)
    {
        _jobId ??= observation.JobId;
        if (!_tenantBound)
        {
            _tenantId = observation.TenantId;
            _tenantBound = true;
        }

        _clientIdentity ??= observation.ClientIdentity;
        _isAuthoritative = observation.IsAuthoritative;
        switch (observation)
        {
            case JobRunShadowObservation run:
                _startedBy ??= run.StartedBy;
                _status = run.Status;
                _currentStepOrdinal = run.CurrentStepOrdinal;
                _createdAtUtc ??= run.CreatedAtUtc;
                _startedAtUtc = run.StartedAtUtc ?? _startedAtUtc;
                _completedAtUtc = run.CompletedAtUtc ?? _completedAtUtc;
                AddHistory(new(
                    run.SourceEventId,
                    JobShadowObservationKind.Run,
                    run.Status,
                    null,
                    null,
                    run.Timestamp));
                break;
            case JobStepShadowObservation step:
                if (!_steps.TryGetValue(step.JobStepRunId, out var stepState))
                {
                    stepState = new MutableStepState(step.JobStepRunId);
                    _steps.Add(step.JobStepRunId, stepState);
                }

                stepState.JobStepId ??= step.JobStepId;
                stepState.Ordinal = step.Ordinal;
                stepState.Status = step.Status;
                stepState.TaskRequestId = step.TaskRequestId ?? stepState.TaskRequestId;
                stepState.CommandCorrelationStatus = correlationStatus;
                stepState.CorrelatedCommandStatus = commandStatus;
                stepState.StartedAtUtc = step.StartedAtUtc ?? stepState.StartedAtUtc;
                stepState.CompletedAtUtc = step.CompletedAtUtc ?? stepState.CompletedAtUtc;
                stepState.LastAcceptedSourceEventId = step.SourceEventId;
                AddHistory(new(
                    step.SourceEventId,
                    JobShadowObservationKind.Step,
                    null,
                    step.Status,
                    step.JobStepRunId,
                    step.Timestamp));
                break;
        }

        _lastObservation = observation;
        _lastAcceptedSourceEventId = observation.SourceEventId;
    }

    private void AddHistory(JobShadowHistoryEntry entry)
    {
        if (_recentHistory.Count == HistoryLimit)
        {
            _recentHistory.RemoveAt(0);
        }

        _recentHistory.Add(entry);
    }

    private JobShadowMessageResult CreateResult(
        JobShadowMessageDisposition disposition,
        JobCommandCorrelationStatus correlationStatus,
        NetRatel.Application.Commands.CommandLifecycleStatus? commandStatus) =>
        new(
            _jobRunId,
            disposition,
            _status,
            _lastAcceptedSourceEventId,
            correlationStatus,
            commandStatus);

    private JobRunShadowState CreateState() =>
        new(
            _jobRunId,
            _jobId,
            _tenantId,
            _clientIdentity,
            _startedBy,
            _status,
            _currentStepOrdinal,
            _createdAtUtc,
            _startedAtUtc,
            _completedAtUtc,
            _lastAcceptedSourceEventId,
            _steps.Values
                .OrderBy(item => item.Ordinal)
                .ThenBy(item => item.JobStepRunId)
                .Select(item => item.ToState())
                .ToArray(),
            _recentHistory.ToArray(),
            _isAuthoritative ? "akka" : "akka-shadow",
            IsAuthoritative: _isAuthoritative);

    private int CountActiveSteps() =>
        _steps.Values.Count(item => item.Status == JobStepRunState.Running);

    private void ResetState()
    {
        _lastObservation = null;
        _jobId = null;
        _tenantId = null;
        _tenantBound = false;
        _isAuthoritative = false;
        _clientIdentity = null;
        _startedBy = null;
        _status = null;
        _currentStepOrdinal = 0;
        _createdAtUtc = null;
        _startedAtUtc = null;
        _completedAtUtc = null;
        _lastAcceptedSourceEventId = 0;
        _steps.Clear();
        _recentHistory.Clear();
    }

    private static bool IsTerminal(JobRunState? status) =>
        status is JobRunState.Succeeded or
            JobRunState.Failed or
            JobRunState.Cancelled or
            JobRunState.TimedOut;

    private sealed class MutableStepState(ulong jobStepRunId)
    {
        public ulong JobStepRunId { get; } = jobStepRunId;
        public ulong? JobStepId { get; set; }
        public int Ordinal { get; set; }
        public JobStepRunState Status { get; set; }
        public string? TaskRequestId { get; set; }
        public JobCommandCorrelationStatus CommandCorrelationStatus { get; set; }
        public NetRatel.Application.Commands.CommandLifecycleStatus? CorrelatedCommandStatus { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public long LastAcceptedSourceEventId { get; set; }

        public JobStepShadowState ToState() =>
            new(
                JobStepRunId,
                JobStepId,
                Ordinal,
                Status,
                TaskRequestId,
                CommandCorrelationStatus,
                CorrelatedCommandStatus,
                StartedAtUtc,
                CompletedAtUtc,
                LastAcceptedSourceEventId);
    }
}

internal sealed record RoutedJobShadowRecord(
    RecordJobShadowObservation Message,
    IActorRef ReplyTo);

internal sealed record RoutedJobShadowResult(
    JobShadowMessageResult Result,
    IActorRef ReplyTo,
    JobRunState? PreviousStatus,
    JobRunState? CurrentStatus,
    int PreviousActiveSteps,
    int CurrentActiveSteps,
    bool RecoveredFromHistory);

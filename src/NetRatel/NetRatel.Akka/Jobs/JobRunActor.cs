using Akka.Actor;
using NetRatel.Application.Jobs;

namespace NetRatel.Akka.Jobs;

/// <summary>
/// Reconstructs and validates one job run from its append-only observation history.
/// Owned mutations retain this mailbox while their scoped persistence and
/// transport operations complete; observation replay never dispatches work.
/// </summary>
public sealed class JobRunActor : ReceiveActor
{
    private const int HistoryLimit = 32;
    private readonly ulong _jobRunId;
    private readonly IJobObservationStore _persistenceStore;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<ulong, MutableStepState> _steps = [];
    private readonly List<JobHistoryEntry> _recentHistory = new(HistoryLimit);
    private IJobObservation? _lastObservation;
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

    public JobRunActor(ulong jobRunId, IJobObservationStore persistenceStore)
    {
        if (jobRunId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(jobRunId));
        }

        _jobRunId = jobRunId;
        _persistenceStore = persistenceStore ?? throw new ArgumentNullException(nameof(persistenceStore));

        ReceiveAsync<ExecuteOwnedJobRun>(async message =>
        {
            var replyTo = Sender;
            var parent = Context.Parent;
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, message.CancellationToken);
            try
            {
                if (message.JobRunId != _jobRunId) throw new InvalidOperationException("The mutation targets another job run.");
                operationCancellation.Token.ThrowIfCancellationRequested();
                var owner = new OwnedRun(this, parent);
                var result = await message.Operation(owner, operationCancellation.Token).ConfigureAwait(false);
                owner.PublishDiagnostics();
                replyTo.Tell(result ?? new OwnedJobRunCompleted());
            }
            catch (Exception error)
            {
                // A relational rollback must also discard observations applied
                // in this receive. The next owner reloads committed history.
                ResetState();
                _recovered = false;
                replyTo.Tell(new Status.Failure(error));
            }
        });

        ReceiveAsync<RoutedJobObservation>(async message =>
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
                parent.Tell(new RoutedJobObservationResult(
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
                parent.Tell(new RoutedJobObservationResult(
                    CreateResult(
                        JobMessageDisposition.PersistenceUnavailable,
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
        ReceiveAsync<GetJobRunProjection>(async message =>
        {
            var replyTo = Sender;
            try
            {
                await EnsureRecoveredAsync().ConfigureAwait(false);
                replyTo.Tell(message.JobRunId == _jobRunId ? CreateState() : EmptyState(message.JobRunId));
            }
            catch (Exception) when (!_stopping.IsCancellationRequested)
            {
                replyTo.Tell(EmptyState(message.JobRunId, "akka-persistence-unavailable"));
            }
        });
    }

    public static Props Props(ulong jobRunId, IJobObservationStore persistenceStore) =>
        global::Akka.Actor.Props.Create(() => new JobRunActor(jobRunId, persistenceStore));

    protected override void PostStop()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        base.PostStop();
    }

    internal static JobRunView EmptyState(
        ulong jobRunId,
        string source = "unobserved") =>
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

    private async Task<JobMessageResult> RecordAsync(IJobObservation observation, IJobObservationStore? persistence = null, CancellationToken cancellationToken = default)
    {
        persistence ??= _persistenceStore;
        if (cancellationToken == default) cancellationToken = _stopping.Token;
        try
        {
            var disposition = GetDisposition(observation);
            var correlationStatus = JobCommandCorrelationStatus.NotProvided;
            NetRatel.Application.Commands.CommandLifecycleStatus? commandStatus = null;

            if (disposition is JobMessageDisposition.Accepted or JobMessageDisposition.Duplicate)
            {
                var written = await persistence.RecordAsync(observation, cancellationToken)
                    .ConfigureAwait(false);
                correlationStatus = written.CommandCorrelationStatus;
                commandStatus = written.CorrelatedCommandStatus;
                if (written.Disposition == JobObservationWriteDisposition.Duplicate &&
                    disposition == JobMessageDisposition.Accepted)
                {
                    ResetState();
                    _recovered = false;
                    await EnsureRecoveredAsync(persistence, cancellationToken).ConfigureAwait(false);
                    disposition = JobMessageDisposition.Duplicate;
                }
            }

            if (disposition == JobMessageDisposition.Accepted)
            {
                Apply(observation, correlationStatus, commandStatus);
            }

            return CreateResult(disposition, correlationStatus, commandStatus);
        }
        catch (Exception) when (!_stopping.IsCancellationRequested)
        {
            return CreateResult(
                JobMessageDisposition.PersistenceUnavailable,
                JobCommandCorrelationStatus.NotProvided,
                null);
        }
    }

    private JobMessageDisposition GetDisposition(IJobObservation observation)
    {
        if (observation.JobRunId != _jobRunId ||
            (_jobId.HasValue && observation.JobId != _jobId.Value) ||
            (_tenantBound && observation.TenantId != _tenantId) ||
            (_clientIdentity is not null &&
             !string.Equals(observation.ClientIdentity, _clientIdentity, StringComparison.Ordinal)))
        {
            return JobMessageDisposition.IdentityMismatch;
        }

        if (_lastObservation is not null && observation.SourceEventId == _lastAcceptedSourceEventId)
        {
            return Equals(observation, _lastObservation)
                ? JobMessageDisposition.Duplicate
                : JobMessageDisposition.IdentityMismatch;
        }

        if (_lastObservation is not null && observation.SourceEventId < _lastAcceptedSourceEventId)
        {
            return JobMessageDisposition.StaleEvent;
        }

        return observation switch
        {
            JobRunObservation run => IsValidRunObservation(run)
                ? JobMessageDisposition.Accepted
                : JobMessageDisposition.InvalidTransition,
            JobStepObservation step => IsValidStepObservation(step)
                ? JobMessageDisposition.Accepted
                : JobMessageDisposition.InvalidTransition,
            _ => JobMessageDisposition.IdentityMismatch
        };
    }

    private bool IsValidRunObservation(JobRunObservation run)
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

    private bool IsValidStepObservation(JobStepObservation step)
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
            (JobStepRunState.Pending, JobStepRunState.Failed) => true,
            (JobStepRunState.Running, JobStepRunState.Running) => true,
            (JobStepRunState.Running, JobStepRunState.Succeeded) => true,
            (JobStepRunState.Running, JobStepRunState.Failed) => true,
            _ => false
        };
    }

    private async Task<bool> EnsureRecoveredAsync(IJobObservationStore? persistence = null, CancellationToken cancellationToken = default)
    {
        if (_recovered)
        {
            return false;
        }

        persistence ??= _persistenceStore;
        if (cancellationToken == default) cancellationToken = _stopping.Token;
        var replay = await persistence.ReplayAsync(_jobRunId, cancellationToken)
            .ConfigureAwait(false);
        ResetState();
        try
        {
            foreach (var persisted in replay)
            {
                if (GetDisposition(persisted.Observation) != JobMessageDisposition.Accepted)
                {
                    throw new InvalidOperationException(
                        $"Durable job history for run '{_jobRunId}' contains an invalid transition.");
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
        persistence.RecordRecoverySucceeded();
        return replay.Count > 0;
    }

    private void Apply(
        IJobObservation observation,
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
            case JobRunObservation run:
                _startedBy ??= run.StartedBy;
                _status = run.Status;
                _currentStepOrdinal = run.CurrentStepOrdinal;
                _createdAtUtc ??= run.CreatedAtUtc;
                _startedAtUtc = run.StartedAtUtc ?? _startedAtUtc;
                _completedAtUtc = run.CompletedAtUtc ?? _completedAtUtc;
                AddHistory(new(
                    run.SourceEventId,
                    JobObservationKind.Run,
                    run.Status,
                    null,
                    null,
                    run.Timestamp));
                break;
            case JobStepObservation step:
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
                    JobObservationKind.Step,
                    null,
                    step.Status,
                    step.JobStepRunId,
                    step.Timestamp));
                break;
        }

        _lastObservation = observation;
        _lastAcceptedSourceEventId = observation.SourceEventId;
    }

    private void AddHistory(JobHistoryEntry entry)
    {
        if (_recentHistory.Count == HistoryLimit)
        {
            _recentHistory.RemoveAt(0);
        }

        _recentHistory.Add(entry);
    }

    private JobMessageResult CreateResult(
        JobMessageDisposition disposition,
        JobCommandCorrelationStatus correlationStatus,
        NetRatel.Application.Commands.CommandLifecycleStatus? commandStatus) =>
        new(
            _jobRunId,
            disposition,
            _status,
            _lastAcceptedSourceEventId,
            correlationStatus,
            commandStatus);

    private JobRunView CreateState() =>
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
            "akka",
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

        public JobStepView ToState() =>
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

    private sealed class OwnedRun(JobRunActor actor, IActorRef parent) : IJobRunOwner
    {
        private readonly List<RoutedJobObservationResult> _diagnostics = [];
        public void PublishDiagnostics()
        {
            foreach (var result in _diagnostics) parent.Tell(result);
        }
        public async Task ReloadAsync(IJobObservationStore persistence, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            actor.ResetState();
            actor._recovered = false;
            await actor.EnsureRecoveredAsync(persistence, cancellationToken).ConfigureAwait(false);
        }

        public async Task<JobMessageResult> RecordAsync(IJobObservation observation, IJobObservationStore persistence, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previousStatus = actor._status;
            var previousSteps = actor.CountActiveSteps();
            var result = await actor.RecordAsync(observation, persistence, cancellationToken).ConfigureAwait(false);
            _diagnostics.Add(new RoutedJobObservationResult(result, ActorRefs.Nobody, previousStatus, actor._status,
                previousSteps, actor.CountActiveSteps(), RecoveredFromHistory: false));
            return result;
        }
    }
}

internal sealed record OwnedJobRunCompleted;

internal sealed record RoutedJobObservation(
    RecordJobObservation Message,
    IActorRef ReplyTo);

internal sealed record RoutedJobObservationResult(
    JobMessageResult Result,
    IActorRef ReplyTo,
    JobRunState? PreviousStatus,
    JobRunState? CurrentStatus,
    int PreviousActiveSteps,
    int CurrentActiveSteps,
    bool RecoveredFromHistory);

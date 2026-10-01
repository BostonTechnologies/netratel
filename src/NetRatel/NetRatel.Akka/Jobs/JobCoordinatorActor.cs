using Akka.Actor;
using NetRatel.Akka.Observability;
using NetRatel.Application.Jobs;

namespace NetRatel.Akka.Jobs;

/// <summary>
/// Local job-observation region. It owns routing and bounded scalar diagnostics;
/// PostgreSQL persists the observation ledger while the authenticated API job
/// authority owns scheduling and execution transitions.
/// </summary>
public sealed class JobCoordinatorActor : ReceiveActor
{
    private readonly IJobObservationStore _persistenceStore;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private int _activeJobs;
    private ulong _completedJobs;
    private ulong _failedJobs;
    private int _activeJobSteps;
    private ulong _acceptedEvents;
    private ulong _invalidTransitions;
    private ulong _duplicateEvents;
    private ulong _staleEvents;
    private ulong _missingCommandCorrelations;

    public JobCoordinatorActor(IJobObservationStore persistenceStore)
    {
        _persistenceStore = persistenceStore ?? throw new ArgumentNullException(nameof(persistenceStore));
        Receive<RecordJobObservation>(message =>
        {
            if (message.Observation.JobRunId == 0)
            {
                Sender.Tell(new JobMessageResult(
                    0,
                    JobMessageDisposition.IdentityMismatch,
                    null,
                    0,
                    JobCommandCorrelationStatus.NotProvided,
                    null));
                return;
            }

            GetOrCreateRunActor(message.Observation.JobRunId)
                .Tell(new RoutedJobObservation(message, Sender));
        });
        Receive<GetJobRunProjection>(message =>
        {
            var actor = GetRunActor(message.JobRunId);
            if (actor.IsNobody())
            {
                actor = GetOrCreateRunActor(message.JobRunId);
            }

            actor.Forward(message);
        });
        Receive<RoutedJobObservationResult>(HandleResult);
        Receive<ProbeJobRuntime>(_ => Sender.Tell(CreateStatus()));
    }

    public static Props Props(IJobObservationStore persistenceStore) =>
        global::Akka.Actor.Props.Create(() => new JobCoordinatorActor(persistenceStore));

    private IActorRef GetOrCreateRunActor(ulong jobRunId)
    {
        var existing = GetRunActor(jobRunId);
        if (!existing.IsNobody())
        {
            return existing;
        }

        using var activity = NetRatelAkkaTelemetry.StartActivity("akka.actor.create", "job");
        return Context.ActorOf(JobRunActor.Props(jobRunId, _persistenceStore), CreateActorName(jobRunId));
    }

    private IActorRef GetRunActor(ulong jobRunId) =>
        Context.Child(CreateActorName(jobRunId));

    private void HandleResult(RoutedJobObservationResult message)
    {
        if (message.RecoveredFromHistory)
        {
            NetRatelAkkaTelemetry.JobReplayed();
            AddRecoveredState(message.PreviousStatus, message.PreviousActiveSteps);
        }

        switch (message.Result.Disposition)
        {
            case JobMessageDisposition.Accepted:
                _acceptedEvents = IncrementSaturating(_acceptedEvents);
                UpdateRunCounts(message.PreviousStatus, message.CurrentStatus);
                UpdateActiveStepCount(message.PreviousActiveSteps, message.CurrentActiveSteps);
                if (message.Result.CommandCorrelationStatus == JobCommandCorrelationStatus.Missing)
                {
                    _missingCommandCorrelations = IncrementSaturating(_missingCommandCorrelations);
                }

                break;
            case JobMessageDisposition.Duplicate:
                _duplicateEvents = IncrementSaturating(_duplicateEvents);
                break;
            case JobMessageDisposition.StaleEvent:
                _staleEvents = IncrementSaturating(_staleEvents);
                break;
            case JobMessageDisposition.InvalidTransition:
            case JobMessageDisposition.IdentityMismatch:
                _invalidTransitions = IncrementSaturating(_invalidTransitions);
                break;
        }

        message.ReplyTo.Tell(message.Result);
    }

    private void AddRecoveredState(JobRunState? status, int activeSteps)
    {
        if (IsActive(status) && _activeJobs < int.MaxValue)
        {
            _activeJobs++;
        }
        else if (status == JobRunState.Succeeded)
        {
            _completedJobs = IncrementSaturating(_completedJobs);
        }
        else if (IsFailure(status))
        {
            _failedJobs = IncrementSaturating(_failedJobs);
        }

        UpdateActiveStepCount(0, activeSteps);
    }

    private void UpdateRunCounts(JobRunState? previous, JobRunState? current)
    {
        if (!IsActive(previous) && IsActive(current) && _activeJobs < int.MaxValue)
        {
            _activeJobs++;
        }
        else if (IsActive(previous) && IsTerminal(current) && _activeJobs > 0)
        {
            _activeJobs--;
        }

        if (current == JobRunState.Succeeded && previous != JobRunState.Succeeded)
        {
            NetRatelAkkaTelemetry.JobCompleted();
            _completedJobs = IncrementSaturating(_completedJobs);
        }
        else if (IsFailure(current) && !IsFailure(previous))
        {
            NetRatelAkkaTelemetry.JobFailed();
            _failedJobs = IncrementSaturating(_failedJobs);
        }

        if (!IsActive(previous) && IsActive(current))
        {
            NetRatelAkkaTelemetry.JobStarted();
        }

        NetRatelAkkaTelemetry.SetJobsActive(_activeJobs);
    }

    private void UpdateActiveStepCount(int previous, int current)
    {
        var delta = current - previous;
        if (delta > 0)
        {
            _activeJobSteps = _activeJobSteps > int.MaxValue - delta
                ? int.MaxValue
                : _activeJobSteps + delta;
        }
        else if (delta < 0)
        {
            _activeJobSteps = Math.Max(0, _activeJobSteps + delta);
        }
    }

    private JobRuntimeStatus CreateStatus() =>
        new(
            _activeJobs,
            _completedJobs,
            _failedJobs,
            _activeJobSteps,
            _acceptedEvents,
            _invalidTransitions,
            _duplicateEvents,
            _staleEvents,
            _missingCommandCorrelations,
            _startedAtUtc,
            "akka",
            "akka");

    private static bool IsActive(JobRunState? status) =>
        status is JobRunState.Pending or JobRunState.Running;

    private static bool IsFailure(JobRunState? status) =>
        status is JobRunState.Failed or JobRunState.TimedOut;

    private static bool IsTerminal(JobRunState? status) =>
        status is JobRunState.Succeeded or
            JobRunState.Failed or
            JobRunState.Cancelled or
            JobRunState.TimedOut;

    private static ulong IncrementSaturating(ulong value) =>
        value == ulong.MaxValue ? value : value + 1;

    private static string CreateActorName(ulong jobRunId) => $"job-run-{jobRunId}";
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;

namespace NetRatel.Infrastructure.Persistence;

public sealed class JobObservationStore(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IJobObservationStore
{
    private const string SourceIdentityConstraint = "UX_JobShadowObservation_Source";
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private long _duplicateDetectionCount;
    private long _replayCount;
    private long _recoverySuccessCount;
    private long _lastPersistedUtcTicks;
    private long _lastReplayUtcTicks;

    public async Task<JobObservationWriteResult> RecordAsync(
        IJobObservation observation,
        CancellationToken cancellationToken)
    {
        Validate(observation);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var existing = await db.JobShadowObservations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.SourceSystem == observation.SourceSystem &&
                        item.SourceEventId == observation.SourceEventId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            IncrementSaturating(ref _duplicateDetectionCount);
            return ToDuplicate(existing);
        }

        var correlation = await ResolveCommandCorrelationAsync(db, observation, cancellationToken)
            .ConfigureAwait(false);
        var recordedAtUtc = _timeProvider.GetUtcNow();
        db.JobShadowObservations.Add(ToRecord(observation, correlation, recordedAtUtc));

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastPersistedUtcTicks, recordedAtUtc.UtcTicks);
            return new(
                observation.JobRunId,
                JobObservationWriteDisposition.Stored,
                correlation.Status,
                correlation.CommandStatus);
        }
        catch (DbUpdateException exception) when (IsConcurrentDuplicate(exception))
        {
            IncrementSaturating(ref _duplicateDetectionCount);
            return new(
                observation.JobRunId,
                JobObservationWriteDisposition.Duplicate,
                correlation.Status,
                correlation.CommandStatus);
        }
    }

    public async Task<IReadOnlyList<PersistedJobObservation>> ReplayAsync(
        ulong jobRunId,
        CancellationToken cancellationToken)
    {
        if (jobRunId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(jobRunId));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var records = await db.JobShadowObservations
            .AsNoTracking()
            .Where(item => item.JobRunId == jobRunId)
            .OrderBy(item => item.SourceEventId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var replayedAtUtc = _timeProvider.GetUtcNow();
        IncrementSaturating(ref _replayCount);
        Interlocked.Exchange(ref _lastReplayUtcTicks, replayedAtUtc.UtcTicks);
        return records.Select(ToObservation).ToArray();
    }

    public async Task<JobObservationDiagnostics> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var observationCount = await db.JobShadowObservations.LongCountAsync(cancellationToken)
            .ConfigureAwait(false);
        var missingCorrelationCount = await db.JobShadowObservations.LongCountAsync(
                item => item.CommandCorrelationStatus == JobCommandCorrelationStatus.Missing,
                cancellationToken)
            .ConfigureAwait(false);
        var lastPersistedTicks = Interlocked.Read(ref _lastPersistedUtcTicks);
        var lastReplayTicks = Interlocked.Read(ref _lastReplayUtcTicks);

        return new(
            observationCount,
            ReadCounter(ref _replayCount),
            ReadCounter(ref _duplicateDetectionCount),
            checked((ulong)Math.Max(0, missingCorrelationCount)),
            ReadCounter(ref _recoverySuccessCount),
            ToTimestamp(lastPersistedTicks),
            ToTimestamp(lastReplayTicks),
            "akka",
            "akka");
    }

    public void RecordRecoverySucceeded() =>
        IncrementSaturating(ref _recoverySuccessCount);

    internal static async Task<CommandCorrelation> ResolveCommandCorrelationAsync(
        OrchestratorDbContext db,
        IJobObservation observation,
        CancellationToken cancellationToken)
    {
        if (observation is not JobStepObservation { TaskRequestId: { Length: > 0 } taskRequestId })
        {
            return new(JobCommandCorrelationStatus.NotProvided, null);
        }

        if (!observation.TenantId.HasValue)
        {
            return new(JobCommandCorrelationStatus.Missing, null);
        }

        var commandStatus = await db.CommandIntentEvents
            .AsNoTracking()
            .Where(item => item.TenantId == observation.TenantId.Value &&
                           item.CommandId == taskRequestId)
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.Sequence)
            .Select(item => (CommandLifecycleStatus?)item.Status)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return commandStatus.HasValue
            ? new(JobCommandCorrelationStatus.Matched, commandStatus)
            : new(JobCommandCorrelationStatus.Missing, null);
    }

    internal static JobShadowObservationRecord ToRecord(
        IJobObservation observation,
        CommandCorrelation correlation,
        DateTimeOffset recordedAtUtc)
    {
        var record = new JobShadowObservationRecord
        {
            Id = Guid.NewGuid(),
            SourceSystem = observation.SourceSystem,
            SourceEventId = observation.SourceEventId,
            JobRunId = observation.JobRunId,
            JobId = observation.JobId,
            TenantId = observation.TenantId,
            ClientIdentity = observation.ClientIdentity,
            Kind = observation.Kind,
            CommandCorrelationStatus = correlation.Status,
            CorrelatedCommandStatus = correlation.CommandStatus,
            ObservedAtUtc = observation.Timestamp,
            IsAuthoritative = observation.IsAuthoritative,
            RecordedAtUtc = recordedAtUtc
        };

        switch (observation)
        {
            case JobRunObservation run:
                record.StartedBy = run.StartedBy;
                record.RunStatus = run.Status;
                record.CurrentStepOrdinal = run.CurrentStepOrdinal;
                record.RunCreatedAtUtc = run.CreatedAtUtc;
                record.StartedAtUtc = run.StartedAtUtc;
                record.CompletedAtUtc = run.CompletedAtUtc;
                break;
            case JobStepObservation step:
                record.JobStepRunId = step.JobStepRunId;
                record.JobStepId = step.JobStepId;
                record.StepStatus = step.Status;
                record.StepOrdinal = step.Ordinal;
                record.TaskRequestId = step.TaskRequestId;
                record.StartedAtUtc = step.StartedAtUtc;
                record.CompletedAtUtc = step.CompletedAtUtc;
                break;
            default:
                throw new ArgumentException("Unsupported job observation type.", nameof(observation));
        }

        return record;
    }

    internal static PersistedJobObservation ToObservation(JobShadowObservationRecord record)
    {
        IJobObservation observation = record.Kind switch
        {
            JobObservationKind.Run when record.RunStatus.HasValue => new JobRunObservation(
                record.SourceEventId,
                checked((ulong)record.JobRunId),
                checked((ulong)record.JobId),
                record.TenantId,
                record.ClientIdentity,
                record.StartedBy ?? "unknown",
                record.RunStatus.Value,
                record.CurrentStepOrdinal ?? 0,
                record.RunCreatedAtUtc ?? record.ObservedAtUtc,
                record.StartedAtUtc,
                record.CompletedAtUtc,
                record.ObservedAtUtc,
                record.SourceSystem,
                record.IsAuthoritative),
            JobObservationKind.Step when record.StepStatus.HasValue && record.JobStepRunId.HasValue =>
                new JobStepObservation(
                    record.SourceEventId,
                    checked((ulong)record.JobRunId),
                    checked((ulong)record.JobId),
                    record.TenantId,
                    record.ClientIdentity,
                    checked((ulong)record.JobStepRunId.Value),
                    record.JobStepId.HasValue ? checked((ulong)record.JobStepId.Value) : null,
                    record.StepStatus.Value,
                    record.StepOrdinal ?? 0,
                    record.TaskRequestId,
                    record.StartedAtUtc,
                    record.CompletedAtUtc,
                    record.ObservedAtUtc,
                    record.SourceSystem,
                    record.IsAuthoritative),
            _ => throw new InvalidOperationException(
                $"Job observation '{record.Id}' is incomplete.")
        };

        return new(observation, record.CommandCorrelationStatus, record.CorrelatedCommandStatus);
    }

    private static JobObservationWriteResult ToDuplicate(JobShadowObservationRecord existing) =>
        new(
            checked((ulong)existing.JobRunId),
            JobObservationWriteDisposition.Duplicate,
            existing.CommandCorrelationStatus,
            existing.CorrelatedCommandStatus);

    internal static void Validate(IJobObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.SourceEventId <= 0 || observation.JobRunId == 0 || observation.JobId == 0 ||
            string.IsNullOrWhiteSpace(observation.SourceSystem) ||
            string.IsNullOrWhiteSpace(observation.ClientIdentity))
        {
            throw new ArgumentException("A complete job observation identity is required.", nameof(observation));
        }

        if (observation is JobStepObservation step &&
            (step.JobStepRunId == 0 || step.Ordinal < 0))
        {
            throw new ArgumentException("A valid job step observation is required.", nameof(observation));
        }
    }

    private static bool IsConcurrentDuplicate(DbUpdateException exception) =>
        DatabaseExceptionClassifier.IsUniqueViolation(exception, SourceIdentityConstraint);

    private static DateTimeOffset? ToTimestamp(long utcTicks) =>
        utcTicks == 0 ? null : new DateTimeOffset(utcTicks, TimeSpan.Zero);

    private static ulong ReadCounter(ref long counter) =>
        checked((ulong)Math.Max(0, Interlocked.Read(ref counter)));

    private static void IncrementSaturating(ref long counter)
    {
        while (true)
        {
            var current = Interlocked.Read(ref counter);
            if (current == long.MaxValue)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref counter, current + 1, current) == current)
            {
                return;
            }
        }
    }

    internal sealed record CommandCorrelation(
        JobCommandCorrelationStatus Status,
        CommandLifecycleStatus? CommandStatus);
}

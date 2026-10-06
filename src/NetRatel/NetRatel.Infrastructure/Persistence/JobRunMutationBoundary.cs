using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.Application.Jobs;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// A session advisory lock serializes one run across API replicas, including
/// the interval between a committed dispatch intent and transient gateway IO.
/// Each projection and its accepted observations share one transaction.
/// A crashed connection releases the lock; recovery never replays physical IO.
/// </summary>
public sealed class JobRunMutationBoundary : IAsyncDisposable
{
    private readonly OrchestratorDbContext _db;
    private readonly long _lockId;
    private IDbContextTransaction? _transaction;
    private bool _locked;
    public IJobObservationStore Observations { get; }

    private JobRunMutationBoundary(OrchestratorDbContext db, ulong runId, TimeProvider clock)
    {
        _db = db;
        _lockId = -checked((long)runId); // Reserved negative key space for run ownership.
        Observations = new TransactionalObservations(db, clock);
    }

    public static async Task<JobRunMutationBoundary> AcquireAsync(OrchestratorDbContext db, ulong runId,
        TimeProvider clock, CancellationToken ct)
    {
        var boundary = new JobRunMutationBoundary(db, runId, clock);
        if (db.Database.IsRelational())
        {
            if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
                throw new InvalidOperationException("Durable job ownership requires PostgreSQL.");
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({boundary._lockId})", ct);
                boundary._locked = true;
            }
            catch
            {
                // A lost acquisition response can still leave a server-side lock.
                Npgsql.NpgsqlConnection.ClearPool((Npgsql.NpgsqlConnection)db.Database.GetDbConnection());
                await db.Database.CloseConnectionAsync();
                throw;
            }
        }
        // A previous operation may have left tracked snapshots in the request scope.
        db.ChangeTracker.Clear();
        return boundary;
    }

    public async Task BeginAsync(CancellationToken ct)
    {
        if (_transaction is not null) throw new InvalidOperationException("A job mutation transaction is already active.");
        if (_db.Database.IsRelational()) _transaction = await _db.Database.BeginTransactionAsync(ct);
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        if (_transaction is null) return;
        await _transaction.CommitAsync(ct);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    public async ValueTask DisposeAsync()
    {
        var cleanupSeconds = _db.Database.IsRelational() ? _db.Database.GetCommandTimeout() ?? 30 : 30;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, cleanupSeconds)));
        try
        {
            if (_transaction is not null)
            {
                try { await _transaction.RollbackAsync(cleanup.Token); }
                catch
                {
                    if (_locked) Npgsql.NpgsqlConnection.ClearPool((Npgsql.NpgsqlConnection)_db.Database.GetDbConnection());
                    throw;
                }
                finally { await _transaction.DisposeAsync(); }
            }
        }
        finally
        {
            if (_locked)
            {
                try { await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({_lockId})", cleanup.Token); }
                catch
                {
                    Npgsql.NpgsqlConnection.ClearPool((Npgsql.NpgsqlConnection)_db.Database.GetDbConnection());
                    throw;
                }
                finally { await _db.Database.CloseConnectionAsync(); }
            }
            _db.ChangeTracker.Clear();
        }
    }

    private sealed class TransactionalObservations(OrchestratorDbContext db, TimeProvider clock) : IJobObservationStore
    {
        public async Task<JobObservationWriteResult> RecordAsync(IJobObservation observation, CancellationToken cancellationToken)
        {
            JobObservationStore.Validate(observation);
            var existing = await db.JobShadowObservations.AsNoTracking().SingleOrDefaultAsync(x =>
                x.SourceSystem == observation.SourceSystem && x.SourceEventId == observation.SourceEventId, cancellationToken);
            if (existing is not null)
                return new(observation.JobRunId, JobObservationWriteDisposition.Duplicate,
                    existing.CommandCorrelationStatus, existing.CorrelatedCommandStatus);
            var correlation = await JobObservationStore.ResolveCommandCorrelationAsync(db, observation, cancellationToken);
            db.JobShadowObservations.Add(JobObservationStore.ToRecord(observation, correlation, clock.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
            return new(observation.JobRunId, JobObservationWriteDisposition.Stored, correlation.Status, correlation.CommandStatus);
        }

        public async Task<IReadOnlyList<PersistedJobObservation>> ReplayAsync(ulong jobRunId, CancellationToken cancellationToken) =>
            (await db.JobShadowObservations.AsNoTracking().Where(x => x.JobRunId == jobRunId)
                .OrderBy(x => x.SourceEventId).ToArrayAsync(cancellationToken)).Select(JobObservationStore.ToObservation).ToArray();

        public Task<JobObservationDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Diagnostics belong to the shared observation store.");
        public void RecordRecoverySucceeded() { }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>Serializes active definition changes and new run admission with retirement across replicas.</summary>
internal static class JobDefinitionMutationFence
{
    internal static async Task<IDbContextTransaction?> BeginAsync(OrchestratorDbContext db, CancellationToken ct) =>
        db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

    internal static async Task<bool> LockActiveAsync(OrchestratorDbContext db, long jobId, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            if (db.Database.CurrentTransaction is null)
                throw new InvalidOperationException("The job definition fence requires an active transaction.");
            // The no-op row write holds the fence until commit and invalidates stale
            // PostgreSQL transaction snapshots, without changing the definition revision.
            // Read the retirement predicate separately after any lock wait completes.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Jobs\" SET \"UpdatedAtUtc\" = \"UpdatedAtUtc\" WHERE \"Id\" = {jobId}", ct);
        }

        return await db.Jobs.AsNoTracking().AnyAsync(job => job.Id == jobId, ct);
    }
}

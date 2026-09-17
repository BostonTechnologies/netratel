using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>Serializes source tombstones and new job references across API replicas.</summary>
internal static class ScriptReferenceFence
{
    public static Task<IDbContextTransaction?> BeginAsync(OrchestratorDbContext db, CancellationToken ct) =>
        db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? BeginOwnedAsync(db, ct)
            : Task.FromResult<IDbContextTransaction?>(null);

    private static async Task<IDbContextTransaction?> BeginOwnedAsync(OrchestratorDbContext db, CancellationToken ct) =>
        await db.Database.BeginTransactionAsync(ct);

    public static async Task<bool> LockActiveAsync(OrchestratorDbContext db, long scriptId, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            if (db.Database.CurrentTransaction is null)
                throw new InvalidOperationException("The script reference fence requires an active transaction.");
            // A row write holds the fence until the caller commits. Unlike an
            // application lock, it also fences other replicas. The no-op write
            // invalidates stale PostgreSQL transaction snapshots without changing
            // the semantic source revision merely because a job references it.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Scripts\" SET \"SourceRevision\" = \"SourceRevision\" WHERE \"Id\" = {scriptId}", ct);
        }

        // Keep active-source validation for every provider, including test stores.
        return await db.Scripts.AsNoTracking().AnyAsync(script => script.Id == scriptId, ct);
    }
}

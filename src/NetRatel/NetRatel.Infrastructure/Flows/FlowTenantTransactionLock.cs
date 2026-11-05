using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Flows;

/// <summary>
/// One transaction fence for subscription activation and source selection, including definitions not yet
/// inserted. Acquire before Flow definition/source/run locks; native execution owner rows may precede it.
/// </summary>
internal static class FlowTenantTransactionLock
{
    private const int Namespace = 733450001;

    internal static void Acquire(OrchestratorDbContext db, int tenantId)
    {
        if (!db.Database.IsNpgsql()) return;
        RequireTransaction(db);
        db.Database.ExecuteSqlInterpolated($"SELECT pg_advisory_xact_lock({Namespace}, {tenantId})");
    }

    internal static async Task AcquireAsync(OrchestratorDbContext db, int tenantId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        RequireTransaction(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({Namespace}, {tenantId})", ct)
            .ConfigureAwait(false);
    }

    private static void RequireTransaction(OrchestratorDbContext db)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Flow tenant admission requires the caller's transaction.");
    }
}

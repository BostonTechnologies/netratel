using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.Application.Presence;
using Npgsql;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>Serializes removal and identity recovery with the existing admission counter.</summary>
public static class AgentLifecycleLock
{
    public static async Task<Agent?> LockAsync(OrchestratorDbContext db, int tenantId, Guid agentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var client = new ClientKey(tenantId, agentId);
        if (!client.IsValid) throw new ArgumentException("invalid-client-key");
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("agent-lifecycle-lock-needs-caller-transaction");

        if (!db.Database.IsNpgsql())
            return await db.Agents.IgnoreQueryFilters().SingleOrDefaultAsync(agent =>
                agent.TenantId == tenantId && agent.Id == agentId && agent.SupersededAtUtc == null,
                cancellationToken).ConfigureAwait(false);

        // Keep the established order: counter, Agent, then owner. NO KEY UPDATE
        // permits foreign-key checks by ingress already holding an owner lock.
        // A caller may acquire it again before mutating Agent in this transaction.
        await ClientConnectionEpochStore.LockCounterAsync((NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction(), client, 0,
            cancellationToken).ConfigureAwait(false);
        var tracked = db.Agents.Local.FirstOrDefault(agent => agent.TenantId == tenantId && agent.Id == agentId);
        var locked = await db.Agents.FromSqlInterpolated($"""
                SELECT * FROM "Agents" WHERE "TenantId" = {tenantId} AND "Id" = {agentId}
                  AND "SupersededAtUtc" IS NULL FOR NO KEY UPDATE
                """)
            .IgnoreQueryFilters().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        // EF identity resolution can return an entity read before the lock was
        // acquired. Reload under the lock so a committed deletion is observed.
        if (locked is not null && tracked is not null)
            await db.Entry(locked).ReloadAsync(cancellationToken).ConfigureAwait(false);
        return locked;
    }
}

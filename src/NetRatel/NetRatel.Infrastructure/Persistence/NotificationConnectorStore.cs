using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Notifications;
using NetRatel.Shared.Contracts.Notifications;
using Npgsql;

namespace NetRatel.Infrastructure.Persistence;

public sealed class NotificationConnectorStore(OrchestratorDbContext db) : INotificationConnectorStore
{
    public async Task<NotificationConnectorState?> GetAsync(int tenantId, Guid id, CancellationToken ct = default)
    {
        var row = await db.NotificationConnectors.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<NotificationConnectorState>> ListAsync(int tenantId, CancellationToken ct = default)
    {
        var rows = await db.NotificationConnectors.AsNoTracking().Where(x => x.TenantId == tenantId && x.DeletedAtUtc == null)
            .OrderBy(x => x.Id).Take(NotificationConnectorLimits.MaximumConnectorsPerTenant + 1).ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count > NotificationConnectorLimits.MaximumConnectorsPerTenant)
            throw new InvalidOperationException("notification-connector-capacity-exhausted");
        return rows.Select(Map).ToArray();
    }

    public async Task<bool> SaveAsync(NotificationConnectorState state, long expectedRevision, CancellationToken ct = default)
    {
        if (state.TenantId <= 0 || state.Id == Guid.Empty || expectedRevision < 0 || state.Revision != checked(expectedRevision + 1))
            throw new ArgumentException("invalid-notification-connector-state");
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false) : null;
        if (expectedRevision == 0 && db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({733460104}, {state.TenantId})", ct).ConfigureAwait(false);
        var row = await db.NotificationConnectors.AsTracking()
            .SingleOrDefaultAsync(x => x.TenantId == state.TenantId && x.Id == state.Id, ct).ConfigureAwait(false);
        if (row is null && expectedRevision != 0 || row is not null && (row.Revision != expectedRevision || row.DeletedAtUtc is not null))
            return false;
        if (row is null)
        {
            if (await db.NotificationConnectors.CountAsync(x => x.TenantId == state.TenantId && x.DeletedAtUtc == null, ct).ConfigureAwait(false)
                >= NotificationConnectorLimits.MaximumConnectorsPerTenant)
                throw new InvalidOperationException("notification-connector-capacity-exhausted");
            row = new() { TenantId = state.TenantId, Id = state.Id };
            db.NotificationConnectors.Add(row);
        }
        row.Revision = state.Revision;
        row.OwnerPrincipalId = state.OwnerPrincipalId;
        row.ConfigurationJson = JsonSerializer.Serialize(state.Configuration);
        row.ProtectedWebhookUrl = state.ProtectedWebhookUrl;
        row.DeletedAtUtc = state.DeletedAtUtc;
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { return false; }
    }

    private static NotificationConnectorState Map(NotificationConnectorRecord row) => new(row.Id, row.TenantId, row.Revision,
        row.OwnerPrincipalId, JsonSerializer.Deserialize<NotificationConnectorConfiguration>(row.ConfigurationJson)
            ?? throw new InvalidOperationException("invalid-notification-connector-record"), row.ProtectedWebhookUrl, row.DeletedAtUtc);
}

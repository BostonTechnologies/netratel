using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Shared.Contracts.Services;
using Npgsql;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>Scope-per-operation cache writes with optimistic revision and durable ingress fencing.</summary>
public sealed class ClientServicesStore(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : IClientServicesStore
{
    public async Task<ClientServicesState?> LoadAsync(ClientKey client, CancellationToken cancellationToken)
    {
        ValidateClient(client);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var record = await db.ClientServicesSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TenantId == client.TenantId && row.AgentId == client.AgentId, cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : Deserialize(record);
    }

    public async Task<ClientServicesStoreWriteResult> SaveAsync(
        ClientServicesState candidate, long expectedRevision, CancellationToken cancellationToken)
    {
        ValidateClient(candidate.Client);
        if (expectedRevision < 0 || candidate.Revision != checked(expectedRevision + 1))
            throw new ArgumentException("A cache write must advance its expected revision once.", nameof(candidate));
        if (candidate.ConnectionEpoch < 0 || candidate.WatchedServices.Count > ClientServicesLimits.MaximumWatchServices ||
            candidate.MonitoredServiceNames.Count > ClientServicesLimits.MaximumWatchServices ||
            candidate.LastCompleteInventory?.Services.Count > ClientServicesLimits.MaximumServices)
            throw new ArgumentException("Invalid services cache bounds.", nameof(candidate));

        var json = JsonSerializer.Serialize(candidate);
        // The inventory wire bytes are bounded separately. JSON property names and escaped characters
        // increase the stored representation, so cap the whole durable record as well.
        if (Encoding.UTF8.GetByteCount(json) > ClientServicesLimits.MaximumInventoryBytes * 4)
            throw new ArgumentException("Services cache exceeds its storage limit.", nameof(candidate));

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var record = await db.ClientServicesSnapshots.SingleOrDefaultAsync(
            row => row.TenantId == candidate.Client.TenantId && row.AgentId == candidate.Client.AgentId, cancellationToken)
            .ConfigureAwait(false);
        var current = record is null ? ClientServicesState.Empty(candidate.Client) : Deserialize(record);
        if (!CanAdvance(current, candidate, expectedRevision))
            return new(ClientServicesStoreWriteDisposition.Conflict, current);

        if (record is null)
        {
            record = new ClientServicesSnapshotRecord { TenantId = candidate.Client.TenantId, AgentId = candidate.Client.AgentId };
            db.ClientServicesSnapshots.Add(record);
        }
        record.ConnectionEpoch = candidate.ConnectionEpoch;
        record.LastAcceptedSequence = candidate.LastAcceptedSequence;
        record.Revision = candidate.Revision;
        record.StateJson = json;
        record.UpdatedAtUtc = timeProvider.GetUtcNow();
        var advancesIngress = candidate.ConnectionEpoch != current.ConnectionEpoch ||
            candidate.LastAcceptedSequence != current.LastAcceptedSequence;
        var concurrentConflict = false;
        // Lock ordering is always allocator, then services row. New admission allocation cannot
        // advance its fence while an older accepted ingress write is still committing.
        await using (var transaction = advancesIngress ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null)
        {
            if (advancesIngress)
            {
                var allocated = await db.ClientConnectionEpochs.FromSqlInterpolated($"""
                    SELECT * FROM "ClientConnectionEpochs"
                    WHERE "TenantId" = {candidate.Client.TenantId} AND "AgentId" = {candidate.Client.AgentId}
                    FOR SHARE
                    """).AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                // Production admissions always have this row. Allow its absence for isolated
                // persistence fixtures and migration seeding, where no active admission exists.
                if (allocated is not null && candidate.ConnectionEpoch < allocated.LastIssuedEpoch)
                    return new(ClientServicesStoreWriteDisposition.Conflict, current);
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                concurrentConflict = true;
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_ClientServicesSnapshots" })
            {
                if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                concurrentConflict = true;
            }
        }
        // The transaction is disposed before a fresh scope reloads a competing writer's state.
        if (concurrentConflict) return await ReloadConflictAsync(candidate.Client, cancellationToken).ConfigureAwait(false);
        return new(ClientServicesStoreWriteDisposition.Stored, Deserialize(record));
    }

    private async Task<ClientServicesStoreWriteResult> ReloadConflictAsync(ClientKey client, CancellationToken cancellationToken) =>
        new(ClientServicesStoreWriteDisposition.Conflict,
            await LoadAsync(client, cancellationToken).ConfigureAwait(false) ?? ClientServicesState.Empty(client));

    private static bool CanAdvance(ClientServicesState current, ClientServicesState candidate, long expectedRevision) =>
        current.Revision == expectedRevision &&
        candidate.WatchPolicyRevision >= current.WatchPolicyRevision &&
        (candidate.WatchPolicyRevision != current.WatchPolicyRevision ||
         candidate.MonitoredServiceNames.SequenceEqual(current.MonitoredServiceNames, StringComparer.Ordinal)) &&
        candidate.ConnectionEpoch >= current.ConnectionEpoch &&
        (candidate.ConnectionEpoch > current.ConnectionEpoch ||
         candidate.LastAcceptedSequence >= current.LastAcceptedSequence &&
         (current.ConnectionId is null || candidate.ConnectionId == current.ConnectionId));

    private static ClientServicesState Deserialize(ClientServicesSnapshotRecord record)
    {
        var state = JsonSerializer.Deserialize<ClientServicesState>(record.StateJson)
            ?? throw new InvalidOperationException("Invalid services cache record.");
        if (state.Client != new ClientKey(record.TenantId, record.AgentId) ||
            state.ConnectionEpoch != record.ConnectionEpoch || state.LastAcceptedSequence != record.LastAcceptedSequence ||
            state.Revision != record.Revision)
            throw new InvalidOperationException("Services cache identity or fence is inconsistent.");
        return state;
    }

    private static void ValidateClient(ClientKey client)
    {
        if (!client.IsValid) throw new ArgumentException("A valid authenticated client is required.", nameof(client));
    }
}

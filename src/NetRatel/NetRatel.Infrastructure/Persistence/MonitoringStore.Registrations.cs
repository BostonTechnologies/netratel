using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using Npgsql;

namespace NetRatel.Infrastructure.Persistence;

public sealed partial class MonitoringStore
{
    private const int MaximumPendingEvidenceRegistrations = 16;
    private static readonly TimeSpan EvidenceRegistrationBudget = TimeSpan.FromSeconds(5);

    /// <summary>Reserves an immutable retry ordinal before process-local registration. It grants no evidence authority.</summary>
    public async Task<MonitoringEvidenceFence?> ReserveEvidenceRegistrationAsync(ClientKey client, Guid connectionId,
        long connectionEpoch, Guid registrationId, CancellationToken cancellationToken)
    {
        if (!client.IsValid || connectionId == Guid.Empty || connectionEpoch <= 0 || registrationId == Guid.Empty)
            throw new ArgumentException("invalid_evidence_registration");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MonitoringEvidenceRegistrationCounters" ("TenantId","AgentId","LastIssuedOrdinal")
            VALUES ({client.TenantId},{client.AgentId},0) ON CONFLICT ("TenantId","AgentId") DO NOTHING
            """, cancellationToken).ConfigureAwait(false);
        var counter = (await db.Set<MonitoringEvidenceRegistrationCounterRecord>().FromSqlInterpolated($"""
            SELECT * FROM "MonitoringEvidenceRegistrationCounters" WHERE "TenantId"={client.TenantId} AND "AgentId"={client.AgentId} FOR UPDATE
            """).ToListAsync(cancellationToken).ConfigureAwait(false)).Single();
        var now = await EvidenceNowAsync(db, timeProvider, cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "MonitoringEvidenceRegistrationAttempts" WHERE "TenantId"={client.TenantId} AND "AgentId"={client.AgentId}
            AND "RetainUntilUtc"<={now}
            """, cancellationToken).ConfigureAwait(false);
        var prior = await db.Set<MonitoringEvidenceRegistrationAttemptRecord>().SingleOrDefaultAsync(row =>
            row.TenantId == client.TenantId && row.AgentId == client.AgentId && row.RegistrationId == registrationId,
            cancellationToken).ConfigureAwait(false);
        if (prior is not null)
            return prior.ConnectionId == connectionId && prior.ConnectionEpoch == connectionEpoch && prior.Status == 1 && prior.ExpiresAtUtc > now
                ? new(client, connectionId, connectionEpoch, registrationId, prior.RegistrationOrdinal) : null;
        if (await db.Set<MonitoringEvidenceRegistrationAttemptRecord>().CountAsync(row => row.TenantId == client.TenantId &&
            row.AgentId == client.AgentId && row.RetainUntilUtc > now, cancellationToken).ConfigureAwait(false) >= 64) return null;
        if (await db.Set<MonitoringEvidenceRegistrationAttemptRecord>().CountAsync(row => row.TenantId == client.TenantId &&
            row.AgentId == client.AgentId && row.Status == 1 && row.ExpiresAtUtc > now, cancellationToken).ConfigureAwait(false) >= MaximumPendingEvidenceRegistrations)
            return null;
        counter.LastIssuedOrdinal = checked(counter.LastIssuedOrdinal + 1);
        var expires = NormalizeDatabaseTime(now + EvidenceRegistrationBudget);
        db.Set<MonitoringEvidenceRegistrationAttemptRecord>().Add(new()
        {
            TenantId = client.TenantId, AgentId = client.AgentId, ConnectionId = connectionId, ConnectionEpoch = connectionEpoch,
            RegistrationId = registrationId, RegistrationOrdinal = counter.LastIssuedOrdinal, Status = 1,
            CreatedAtUtc = now, ExpiresAtUtc = expires, RetainUntilUtc = expires + EvidenceRegistrationBudget
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(client, connectionId, connectionEpoch, registrationId, counter.LastIssuedOrdinal);
    }

    private static Task<DateTimeOffset> EvidenceNowAsync(OrchestratorDbContext db, TimeProvider clock, CancellationToken ct) =>
        ClientConnectionEpochStore.EffectiveNowAsync((NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction(), clock, ct);
}

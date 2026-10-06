using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Gateway;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services.Monitoring;

/// <summary>Shared evidence reads use committed storage. Only physical Begin checks this process's presented registration.</summary>
public sealed class MonitoringClientDirectory(IMonitoringAgentEligibility eligibility,
    IAgentTelemetryGatewaySessionRegistry sessions, IServiceScopeFactory scopes, TimeProvider clock) : IMonitoringClientDirectory
{
    public Task<ImmutableArray<Guid>> GetEligibleAgentsAsync(int tenantId, CancellationToken cancellationToken) =>
        eligibility.GetEligibleAgentsAsync(tenantId, cancellationToken);
    public Task<bool> IsEligibleAsync(ClientKey client, CancellationToken cancellationToken) =>
        eligibility.IsEligibleAsync(client, cancellationToken);
    public Task<bool> IsPresentedEvidenceAsync(MonitoringEvidenceFence fence, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(sessions.IsPresentedRegistration(fence)); }

    public async Task<MonitoringEvidenceFence?> GetCurrentEvidenceAsync(ClientKey client, CancellationToken cancellationToken)
    {
        if (!client.IsValid) return null;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var observed = await db.MonitoringEvidenceStreams.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == client.TenantId && row.AgentId == client.AgentId, cancellationToken).ConfigureAwait(false);
        if (observed is not { Active: true, CommittedRegistrationOrdinal: > 0 }) return null;
        var owner = new OwnerKey(client, observed.ConnectionId, observed.ConnectionEpoch);
        if (await ClientConnectionEpochStore.LockEffectiveOwnerSnapshotAsync(db, owner, clock, cancellationToken).ConfigureAwait(false) is null)
            return null;
        var locked = (await db.MonitoringEvidenceStreams.FromSqlInterpolated($"""
            SELECT * FROM "MonitoringEvidenceStreams" WHERE "TenantId"={client.TenantId} AND "AgentId"={client.AgentId} FOR SHARE
            """).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (locked is not { Active: true } || locked.ConnectionId != observed.ConnectionId || locked.ConnectionEpoch != observed.ConnectionEpoch ||
            locked.EvidenceStreamId != observed.EvidenceStreamId || locked.CommittedRegistrationOrdinal != observed.CommittedRegistrationOrdinal ||
            await ClientConnectionEpochStore.LockEffectiveOwnerSnapshotAsync(db, owner, clock, cancellationToken).ConfigureAwait(false) is null) return null;
        return new(client, locked.ConnectionId, locked.ConnectionEpoch, locked.EvidenceStreamId, locked.CommittedRegistrationOrdinal);
    }
}

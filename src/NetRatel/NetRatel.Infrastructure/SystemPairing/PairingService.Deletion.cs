using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.SystemPairing;
namespace NetRatel.Infrastructure.SystemPairing;
internal sealed record PairingCleanupPeer(PairingMetadata Peer, string CallerSecretHash);
public sealed partial class PairingService
{
    private async Task RevokeMappingAsync(PairingConnectionRecord row, CancellationToken ct)
    {
        if (row.DeletedAtUtc is not null) return;
        if (row.InboundPrincipalId is { } principal) await principals.RevokeAsync(principal, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"RatelDeskConnectors\" SET \"ConfigurationJson\" = jsonb_set(\"ConfigurationJson\"::jsonb, '{{Enabled}}', 'false'::jsonb) WHERE \"Id\" = {row.Id}", ct);
        await db.Set<RatelDeskConnectorRecord>().Where(x => x.Id == row.Id).ExecuteUpdateAsync(x => x
            .SetProperty(y => y.ProtectedCredential, (string?)null).SetProperty(y => y.ReadinessJson, (string?)null)
            .SetProperty(y => y.AuthenticationJson, (string?)null).SetProperty(y => y.RowVersion, y => y.RowVersion + 1), ct);
        // Keep a minimal nonsecret tombstone so delayed Save completion cannot reinsert authority.
        row.Active = false; row.DeletedAtUtc = clock.GetUtcNow(); row.Revision++; row.MappingJson = "";
        row.ProtectedInboundCredential = null; row.ProtectedOutboundCredential = null; row.LastTestJson = null; row.SaveFingerprint = "";
        row.AdministratorId = "";
    }
    private async Task RevokePairMappingsAsync(string pairId, CancellationToken ct)
    {
        foreach (var row in await db.Set<PairingConnectionRecord>().Where(x => x.PairId == pairId && x.DeletedAtUtc == null).ToListAsync(ct)) await RevokeMappingAsync(row, ct);
    }
    private async Task RemovePairAuthorityAsync(SystemPairRecord pair, CancellationToken ct)
    {
        await RevokePairMappingsAsync(pair.Id, ct);
        pair.DeletedAtUtc = clock.GetUtcNow(); pair.InboundSecretHash = ""; pair.ProtectedInboundSecret = ""; pair.ProtectedOutboundSecret = null; pair.Revision++;
        await db.Set<PairingRedemption>().Where(x => x.PairId == pair.Id).ExecuteDeleteAsync(ct);
        await db.Set<PairingConnectAttempt>().Where(x => x.PairId == pair.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.ProtectedRequest, "").SetProperty(y => y.ExpiresAtUtc, clock.GetUtcNow()), ct);
    }
    private void QueueCleanup(SystemPairRecord pair, Guid? id)
    {
        if (pair.ProtectedOutboundSecret is null) return;
        db.Add(new PairingCleanupRecord { PairId = pair.Id, MappingId = id,
            ProtectedPeerJson = Protect("cleanup-peer", Json(new PairingCleanupPeer(Read<PairingMetadata>(pair.PeerMetadataJson), pair.InboundSecretHash))), ProtectedSecret = Protect("cleanup-secret", Unprotect(pair.Id + "/outbound", pair.ProtectedOutboundSecret)),
            ExpiresAtUtc = clock.GetUtcNow().AddHours(24), NextAttemptAtUtc = clock.GetUtcNow() });
    }
    public async Task DeleteAsync(string pairId, Guid? mappingId, ClaimsPrincipal actor, CancellationToken ct)
    {
        await authority.RequireAdministratorAsync(actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockAsync(ct); db.ChangeTracker.Clear();
        var pair = await db.Set<SystemPairRecord>().SingleOrDefaultAsync(x => x.Id == pairId, ct);
        if (pair is null || pair.DeletedAtUtc is not null) return;
        // Deletion requires current local administrative permission, never current peer/target readiness.
        if (mappingId is { } id)
        {
            var row = await db.Set<PairingConnectionRecord>().SingleOrDefaultAsync(x => x.Id == id && x.PairId == pairId, ct);
            if (row is null || row.DeletedAtUtc is not null) return;
            var mapping = Read<PairingMapping>(row.MappingJson);
            if (!await authority.CanManageAsync(PairingAuthority.ActorId(actor), int.Parse(mapping.NetRatelTenantId, CultureInfo.InvariantCulture), false, false, ct)) throw new PairingException(403, "connection-delete-denied", "Integration management authority is required for this connection's NetRatel tenant.");
            QueueCleanup(pair, id); await RevokeMappingAsync(row, ct);
            await db.SaveChangesAsync(ct);
            if (!await db.Set<PairingConnectionRecord>().AnyAsync(x => x.PairId == pairId && x.DeletedAtUtc == null, ct)) { QueueCleanup(pair, null); await RemovePairAuthorityAsync(pair, ct); }
        }
        else
        {
            var rows = await db.Set<PairingConnectionRecord>().AsNoTracking().Where(x => x.PairId == pairId && x.DeletedAtUtc == null).ToArrayAsync(ct);
            if (rows.Length == 0 && pair.AdministratorId != PairingAuthority.ActorId(actor) && !await authority.CanManageInstanceAsync(actor, ct)) throw new PairingException(403, "pair-delete-denied", "Integration management authority is required for this pairing.");
            foreach (var row in rows)
                if (!await authority.CanManageAsync(PairingAuthority.ActorId(actor), int.Parse(Read<PairingMapping>(row.MappingJson).NetRatelTenantId, CultureInfo.InvariantCulture), false, false, ct)) throw new PairingException(403, "pair-delete-denied", "Integration management authority is required for every tenant mapping in this pairing.");
            QueueCleanup(pair, null); await RemovePairAuthorityAsync(pair, ct);
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task ReceiveDeleteAsync(SystemPairRecord authenticated, Guid? mappingId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockAsync(ct); db.ChangeTracker.Clear();
        var pair = await db.Set<SystemPairRecord>().SingleOrDefaultAsync(x => x.Id == authenticated.Id && x.Revision == authenticated.Revision && x.DeletedAtUtc == null, ct);
        if (pair is null) return;
        if (mappingId is { } id)
        {
            var row = await db.Set<PairingConnectionRecord>().SingleOrDefaultAsync(x => x.Id == id && x.PairId == pair.Id, ct);
            if (row is null)
            {
                // A deletion may overtake the first Save; remember the target ID before it arrives.
                db.Add(new PairingConnectionRecord { Id = id, PairId = pair.Id, DeletedAtUtc = clock.GetUtcNow(), CreatedAtUtc = clock.GetUtcNow() });
            }
            else await RevokeMappingAsync(row, ct);
            await db.SaveChangesAsync(ct);
            if (!await db.Set<PairingConnectionRecord>().AnyAsync(x => x.PairId == pair.Id && x.DeletedAtUtc == null, ct)) await RemovePairAuthorityAsync(pair, ct);
        }
        else await RemovePairAuthorityAsync(pair, ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task CleanupAsync(CancellationToken ct)
    {
        var rows = await db.Set<PairingCleanupRecord>().Where(x => x.NextAttemptAtUtc <= clock.GetUtcNow()).OrderBy(x => x.NextAttemptAtUtc).Take(10).ToListAsync(ct);
        var installed = await identities.GetAsync(ct);
        foreach (var row in rows)
        {
            if (row.ExpiresAtUtc <= clock.GetUtcNow() || row.Attempts >= 6) { db.Remove(row); continue; }
            try
            {
                var cleanup = Read<PairingCleanupPeer>(Unprotect("cleanup-peer", row.ProtectedPeerJson)); var peer = cleanup.Peer;
                _ = await transport.SendAsync<bool>(peer.ApiOrigin, HttpMethod.Delete, row.MappingId is { } id ? "/mappings/" + id.ToString("D") : "/pair", null,
                    Unprotect("cleanup-secret", row.ProtectedSecret), installed.InstanceId.ToString("D"), ct, callerSecretHash: cleanup.CallerSecretHash);
                db.Remove(row);
            }
            catch (PairingException error) when (error.StatusCode is 401 or 404 or 410) { db.Remove(row); }
            catch (Exception error) when (error is PairingException or HttpRequestException or IOException or OperationCanceledException)
            { row.Attempts++; row.NextAttemptAtUtc = clock.GetUtcNow().AddMinutes(Math.Min(60, 1 << row.Attempts)); }
        }
        await db.SaveChangesAsync(ct);
    }
}

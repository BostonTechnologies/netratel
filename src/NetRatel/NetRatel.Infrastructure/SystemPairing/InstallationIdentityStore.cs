using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.SystemPairing;
namespace NetRatel.Infrastructure.SystemPairing;
public sealed class InstallationIdentityStore(OrchestratorDbContext db, IFlowSourceIdentityResolver flow,
    IOptionsMonitor<ServiceIdentityOptions> options)
{
    public async Task<InstallationIdentityRecord> GetAsync(CancellationToken ct)
    {
        var row = await db.Set<InstallationIdentityRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (row is null)
        {
            var configured = options.CurrentValue.InstanceId;
            var bootstrap = await db.BootstrapInitializations.AsNoTracking().Select(x => x.BootstrapInstanceId).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(configured) && (!Guid.TryParseExact(configured, "D", out _) || Guid.Parse(configured) == Guid.Empty))
                throw new PairingException(503, "installation-identity-invalid", "The configured installation identity is invalid.");
            row = new() { InstanceId = !string.IsNullOrEmpty(configured) ? Guid.Parse(configured) : bootstrap != Guid.Empty ? bootstrap : Guid.NewGuid() };
            db.Add(row);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.Entry(row).State = EntityState.Detached; if (!await db.Set<InstallationIdentityRecord>().AnyAsync(x => x.Id == 1, ct)) throw; }
            row = await db.Set<InstallationIdentityRecord>().AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        }
        if (!string.IsNullOrEmpty(options.CurrentValue.InstanceId) && row.InstanceId.ToString("D") != options.CurrentValue.InstanceId)
            throw new PairingException(503, "installation-identity-drift", "Restore the configured immutable installation identity.");
        var source = await flow.EnsureAsync(ct);
        if (row.SourceInstanceId is not null && row.SourceInstanceId != source)
            throw new PairingException(503, "producer-identity-drift", "The existing Flow producer identity differs from this installation's preserved producer.");
        if (row.SourceInstanceId is null)
        {
            await db.Set<InstallationIdentityRecord>().Where(x => x.Id == 1 && x.SourceInstanceId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceInstanceId, source).SetProperty(x => x.Revision, x => x.Revision + 1), ct);
            row = await db.Set<InstallationIdentityRecord>().AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        }
        if (row.SourceInstanceId != source) throw new PairingException(503, "producer-identity-drift", "The preserved producer changed during identity initialization.");
        return row;
    }
}

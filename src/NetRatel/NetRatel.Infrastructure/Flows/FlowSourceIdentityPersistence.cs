using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceLinks;

namespace NetRatel.Infrastructure.Flows;

public sealed partial class FlowPersistenceService
{
    Task<Guid> IFlowSourceIdentityResolver.EnsureAsync(CancellationToken ct) => WithDb(async (db, services) =>
    {
        // Production connector setup always requires the real installation/deployment store.
        _ = await services.GetRequiredService<ServiceLinkIdentityStore>().GetAsync(ct).ConfigureAwait(false);
        await using var tx = await BeginAsync(db, ct).ConfigureAwait(false);
        var source = await EnsureSourceInContextAsync(db, ct).ConfigureAwait(false);
        if (tx is not null) await tx.CommitAsync(ct).ConfigureAwait(false);
        return source;
    });

    private static async Task<Guid> EnsureSourceInContextAsync(OrchestratorDbContext db, CancellationToken ct)
    {
        var existing = await db.FlowRuntimeIdentity.AsNoTracking().Where(row => row.Id == 1)
            .Select(row => (Guid?)row.SourceInstanceId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (existing is { } preserved)
            return preserved != Guid.Empty ? preserved : throw new InvalidOperationException("flow-source-identity");
        // Keep the actual persisted Flow producer when present. For the first producer only,
        // reuse an already explicitly adopted source, otherwise create one durable singleton.
        var adopted = await db.Set<ServiceLinkRuntimeIdentity>().AsNoTracking().Where(row => row.Id == 1)
            .Select(row => row.SourceInstanceId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var proposed = adopted ?? Guid.NewGuid();
        if (proposed == Guid.Empty) throw new InvalidOperationException("flow-source-identity");
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"FlowRuntimeIdentity\" (\"Id\", \"SourceInstanceId\") VALUES (1, {proposed}) ON CONFLICT (\"Id\") DO NOTHING", ct).ConfigureAwait(false);
        else
        {
            // Existing isolated in-memory Flow harness seam; never selected by the production host.
            db.FlowRuntimeIdentity.Add(new() { SourceInstanceId = proposed });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        var winner = await db.FlowRuntimeIdentity.AsNoTracking().Where(row => row.Id == 1)
            .Select(row => row.SourceInstanceId).SingleAsync(ct).ConfigureAwait(false);
        return winner != Guid.Empty ? winner : throw new InvalidOperationException("flow-source-identity");
    }
}

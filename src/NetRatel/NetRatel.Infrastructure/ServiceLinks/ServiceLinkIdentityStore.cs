using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using static NetRatel.Infrastructure.ServiceLinks.ServiceLinkValidation;

namespace NetRatel.Infrastructure.ServiceLinks;

public sealed class ServiceLinkRuntimeIdentity
{
    public int Id { get; set; } = 1;
    public Guid InstanceId { get; set; }
    public Guid? SourceInstanceId { get; set; }
    public long Revision { get; set; } = 1;
    public string? SourceAdoptedBy { get; set; }
    public long? SourceAdoptedAtUnixSeconds { get; set; }
}

/// <summary>The installation identity is independent from an explicitly adopted incident producer.</summary>
public sealed class ServiceLinkIdentityStore(OrchestratorDbContext db, IOptionsMonitor<ServiceIdentityOptions> identity,
    IOptionsMonitor<ServiceLinkOptions> linking, IEffectiveAccessService access, TimeProvider clock)
{
    public async Task<ServiceLinkIdentityDto> GetAsync(CancellationToken ct)
    {
        var row = await db.Set<ServiceLinkRuntimeIdentity>().SingleOrDefaultAsync(x => x.Id == 1, ct);
        var configured = identity.CurrentValue.InstanceId;
        Guid configuredId = default;
        Require(string.IsNullOrEmpty(configured) || Guid.TryParseExact(configured, "D", out configuredId) && configuredId != Guid.Empty && configuredId.ToString("D") == configured,
            "identity-configuration-invalid", "The configured installation identity must be a canonical GUID.", 503);
        if (row is null)
        {
            var bootstrap = await db.BootstrapInitializations.AsNoTracking().Select(x => x.BootstrapInstanceId).FirstOrDefaultAsync(ct);
            row = new() { InstanceId = configuredId != Guid.Empty ? configuredId : bootstrap != Guid.Empty ? bootstrap : Guid.NewGuid() };
            db.Set<ServiceLinkRuntimeIdentity>().Add(row);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // A competing first request may have established the singleton.
                // Reuse its committed identity; never generate a second installation.
                db.Entry(row).State = EntityState.Detached;
                row = await db.Set<ServiceLinkRuntimeIdentity>().SingleOrDefaultAsync(x => x.Id == 1, ct);
                if (row is null) throw;
            }
        }
        Require(configuredId == Guid.Empty || row.InstanceId == configuredId, "identity-configuration-drift",
            "The configured installation identity differs from the persisted installation. Restore its approved identity.", 503);
        if (!string.IsNullOrEmpty(linking.CurrentValue.SourceInstanceId))
        {
            Require(Guid.TryParseExact(linking.CurrentValue.SourceInstanceId, "D", out var source) && source.ToString("D") == linking.CurrentValue.SourceInstanceId,
                "identity-configuration-invalid", "The configured producer identity must be a canonical GUID.", 503);
            await Adopt(row, source, "deployment", row.Revision, ct);
        }
        return Dto(row);
    }

    public async Task<ServiceLinkIdentityDto> AdoptSourceAsync(ClaimsPrincipal actor, Guid sourceInstanceId, long expectedRevision, CancellationToken ct)
    {
        Require(actor.Identity?.IsAuthenticated == true && actor.FindFirst("netratel_integration_credential_id") is null &&
            actor.FindFirst(ServiceIdentityClaims.PrincipalId) is null &&
            await access.AuthorizeAsync(actor, NetRatelPermissions.IntegrationManagement, null, ct),
            "administrator-required", "Instance integration-management authority is required to adopt a producer identity.", 403);
        _ = await GetAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var row = await db.Set<ServiceLinkRuntimeIdentity>().SingleAsync(x => x.Id == 1, ct);
        var actorId = actor.FindFirstValue("netratel_principal_id") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub");
        Require(!string.IsNullOrEmpty(actorId), "administrator-required", "The approving principal is unidentified.", 403);
        await Adopt(row, sourceInstanceId, actorId!, expectedRevision, ct);
        await tx.CommitAsync(ct);
        return Dto(row);
    }

    private async Task Adopt(ServiceLinkRuntimeIdentity row, Guid source, string actor, long expected, CancellationToken ct)
    {
        Require(source != Guid.Empty && row.Revision == expected, "identity-revision-conflict", "Reload the current producer identity revision.", 409);
        if (row.SourceInstanceId == source) return;
        Require(row.SourceInstanceId is null && !await db.Set<ServiceLinkAttempt>().AnyAsync(ct), "source-identity-conflict",
            "An existing producer or approved attempt must be preserved. A different Flow producer requires an explicit mapping and new consent.", 409);
        row.SourceInstanceId = source; row.SourceAdoptedBy = actor; row.SourceAdoptedAtUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds(); row.Revision++;
        await db.SaveChangesAsync(ct);
    }
    private static ServiceLinkIdentityDto Dto(ServiceLinkRuntimeIdentity row) => new(row.InstanceId.ToString("D"), row.SourceInstanceId?.ToString("D"), row.Revision);
}

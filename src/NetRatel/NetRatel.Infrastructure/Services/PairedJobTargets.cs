using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Agents;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Infrastructure.Services;

/// <summary>Definition admission does not require online workers or values for declared runtime inputs.</summary>
public static class PairedJobTargets
{
    public static async Task<Guid?> LegacyAgentAsync(OrchestratorDbContext db, int tenant, Guid? agentId,
        string? identity, CancellationToken ct)
    {
        if (agentId is null && Guid.TryParse(identity, out var parsed)) agentId = parsed;
        if (agentId is null && !string.IsNullOrWhiteSpace(identity))
        {
            var normalized = PrimaryClientAgentBindingService.NormalizePrimaryClientIdentity(identity);
            agentId = await db.PrimaryClientAgentBindings.AsNoTracking().Where(row => row.TenantId == tenant &&
                row.PrimaryClientIdentity == normalized && row.Status == PrimaryClientAgentBindingStatus.Bound)
                .Select(row => row.AgentId).SingleOrDefaultAsync(ct);
        }
        return await db.Agents.AsNoTracking().AnyAsync(row => row.TenantId == tenant && row.Id == agentId &&
            row.IsEnabled && row.Status == AgentStatus.Active && row.RevokedAtUtc == null && row.DeletedAtUtc == null &&
            row.SupersededAtUtc == null && row.SupersededByAgentId == null, ct) ? agentId : null;
    }

    public static async Task<bool> ValidAsync(OrchestratorDbContext db, int? tenant, JobTargetDefinition? target,
        Guid? agentId, string? identity, CancellationToken ct)
    {
        if (tenant is not > 0) return false;
        if (target is null) return await LegacyAgentAsync(db, tenant.Value, agentId, identity, ct) is not null;
        // Discovery/authentication can run inside another admission transaction. It must
        // not hold shared locks over every tenant group while one selected group rotates.
        try { await JobTargetDefinitionGuard.RequireAsync(db, tenant, target, ct, lockDefinition: false); return true; }
        catch (ArgumentException) { return false; }
        catch (JobTargetValidationException) { return false; }
    }
}

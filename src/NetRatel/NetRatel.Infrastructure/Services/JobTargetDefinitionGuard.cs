using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Infrastructure.Services;

internal static class JobTargetDefinitionGuard
{
    internal static async Task RequireAsync(OrchestratorDbContext db, int? tenantId, JobTargetDefinition? target, CancellationToken ct)
    {
        if (target is null) return;
        if (tenantId is not > 0) throw new JobTargetValidationException("target_tenant_required", "A tenant-owned target requires a tenant.");
        target.Validate();
        if (target.Source == JobTargetSource.ExecutionGroup)
        {
            var query = db.Database.IsRelational()
                ? db.ExecutionGroups.FromSqlInterpolated($"SELECT * FROM \"ExecutionGroups\" WHERE \"TenantId\" = {tenantId.Value} AND \"Id\" = {target.ExecutionGroupId!.Value} FOR SHARE")
                : db.ExecutionGroups.Where(x => x.TenantId == tenantId && x.Id == target.ExecutionGroupId);
            if (!await query.AsNoTracking().AnyAsync(x => x.Enabled && !x.Archived, ct).ConfigureAwait(false))
                throw new JobTargetValidationException("target_group_unavailable", "The selected execution group must be enabled in this tenant.");
        }
        else if (target.Source == JobTargetSource.ExplicitNodes)
        {
            var ids = target.AgentIds!.Distinct().ToArray();
            var count = await db.Agents.IgnoreQueryFilters().AsNoTracking().CountAsync(x => x.TenantId == tenantId && ids.Contains(x.Id) &&
                x.IsEnabled && x.Status == AgentStatus.Active && x.DeletedAtUtc == null && x.RevokedAtUtc == null &&
                x.SupersededAtUtc == null && x.SupersededByAgentId == null, ct).ConfigureAwait(false);
            if (count != ids.Length) throw new JobTargetValidationException("target_node_unavailable", "Every selected node must be a current enabled client in the tenant.");
        }
        else if (target.Source == JobTargetSource.NodeFilter) _ = JobNodeFilterParser.Parse(target.Filter);
    }
}

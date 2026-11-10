using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

/// <summary>Round-robin selection serialized with the parent admission, across replicas.</summary>
public sealed class HaGroupWorkerSelector(OrchestratorDbContext db, TimeProvider clock)
{
    public async Task<JobResolvedTargetPlan> SelectAsync(JobResolvedTargetPlan approved, CancellationToken ct)
    {
        if (!approved.NeedsHaWorkerSelection) return approved;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("HA worker selection requires the parent admission transaction.");
        var groupId = approved.Target.ExecutionGroupId!.Value;
        var group = await (db.Database.IsNpgsql()
            ? db.ExecutionGroups.FromSqlInterpolated($"SELECT * FROM \"ExecutionGroups\" WHERE \"TenantId\" = {approved.TenantId} AND \"Id\" = {groupId} FOR UPDATE")
            : db.ExecutionGroups.Where(row => row.TenantId == approved.TenantId && row.Id == groupId))
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (group is null || !group.Enabled || group.Archived || group.Revision != approved.GroupRevision)
            throw new JobTargetValidationException("target_review_stale", "The HA Group changed. Refresh the available workers and try again.", 409);
        var last = group.LastHaWorkerAgentId is { } previous
            ? approved.Nodes.Select(node => node.AgentId).ToList().IndexOf(previous) : -1;
        var candidateIds = approved.Nodes.Select(node => node.AgentId).ToArray();
        var now = clock.GetUtcNow();
        // Use the admission connection: a burst must not exhaust the pool by
        // holding one transaction while asking another connection for presence.
        var healthy = (await db.ClientConnectionOwners.AsNoTracking().Where(owner => owner.TenantId == approved.TenantId &&
            candidateIds.Contains(owner.AgentId) && owner.Active && owner.ConnectionEpoch > 0 && owner.ConnectionId != null &&
            owner.AcceptanceGuardAtUtc != null && owner.PresenceExpiresAtUtc > now && owner.AuthenticationExpiresAtUtc > now)
            .Select(owner => owner.AgentId).ToArrayAsync(ct)).ToHashSet();
        for (var offset = 1; offset <= approved.Nodes.Count; offset++)
        {
            var candidate = approved.Nodes[(last + offset) % approved.Nodes.Count];
            if (!healthy.Contains(candidate.AgentId)) continue;
            if (db.Database.IsRelational())
                await db.ExecutionGroups.Where(row => row.Id == groupId && row.TenantId == approved.TenantId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastHaWorkerAgentId, candidate.AgentId), ct);
            else
            {
                var tracked = await db.ExecutionGroups.FindAsync([groupId], ct);
                tracked!.LastHaWorkerAgentId = candidate.AgentId;
                await db.SaveChangesAsync(ct);
            }
            return approved.SelectHaWorker(candidate.AgentId);
        }
        throw new JobTargetValidationException("ha_group_no_healthy_workers", "No healthy workers are available in this HA Group. Bring a worker online and try again.", 409);
    }
}

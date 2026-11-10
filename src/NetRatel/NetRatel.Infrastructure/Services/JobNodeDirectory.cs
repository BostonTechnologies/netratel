using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Clients;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Infrastructure.Services;

/// <summary>One projection over the canonical directory, never a second client or tag store.</summary>
public sealed class JobNodeDirectory(OrchestratorDbContext db, IClientPresenceReadModel? presence = null) : IJobNodeDirectory
{
    public async Task<IReadOnlyList<JobNodeInventoryDto>> ReadAsync(int tenantId, IReadOnlySet<Guid>? authorizedAgentIds,
        bool includeInvalid, CancellationToken cancellationToken)
    {
        if (tenantId <= 0) throw new JobTargetValidationException("target_tenant_required", "A valid tenant is required.");
        var query = db.Agents.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!includeInvalid) query = query.Where(x => x.IsEnabled && x.Status == AgentStatus.Active &&
            x.RevokedAtUtc == null && x.DeletedAtUtc == null && x.SupersededAtUtc == null && x.SupersededByAgentId == null);
        if (authorizedAgentIds is not null) { var ids = authorizedAgentIds.ToArray(); query = query.Where(x => ids.Contains(x.Id)); }
        var agents = await query.OrderBy(x => x.Id).Take(JobNodeFilterEvaluator.MaximumCandidates + 1)
            .Select(x => new Agent
            {
                Id = x.Id, TenantId = x.TenantId, Name = x.Name, FriendlyName = x.FriendlyName, Tags = x.Tags,
                IsEnabled = x.IsEnabled, Status = x.Status, RevokedAtUtc = x.RevokedAtUtc, DeletedAtUtc = x.DeletedAtUtc,
                SupersededAtUtc = x.SupersededAtUtc, SupersededByAgentId = x.SupersededByAgentId, DeviceInfoJson = x.DeviceInfoJson
            }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (agents.Length > JobNodeFilterEvaluator.MaximumCandidates)
            throw new JobTargetValidationException("target_inventory_capacity_exceeded", "The authorized inventory exceeds 10000 nodes; narrow the tenant scope.");
        var online = presence is null ? new HashSet<Guid>() : (await presence.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Items
            .Where(x => x.Client.TenantId == tenantId && x.IsAuthoritative && x.Status == ClientPresenceStatus.Online &&
                x.AuthenticationExpiresAtUtc > DateTimeOffset.UtcNow).Select(x => x.Client.AgentId).ToHashSet();
        return agents.Select(agent => Map(agent, online.Contains(agent.Id)))
            .OrderBy(x => JobTargetNode.SortKey(x.Hostname, x.Name, x.DisplayName), StringComparer.OrdinalIgnoreCase).ThenBy(x => x.AgentId).ToArray();
    }

    public static JobNodeInventoryDto Map(Agent agent, bool online)
    {
        var identity = ClientDirectoryIdentity.Parse(agent.DeviceInfoJson);
        var reason = agent.DeletedAtUtc is not null ? "agent_deleted" : agent.RevokedAtUtc is not null ? "agent_revoked" :
            agent.SupersededAtUtc is not null || agent.SupersededByAgentId is not null ? "agent_superseded" :
            !agent.IsEnabled || agent.Status != AgentStatus.Active ? "agent_disabled" : null;
        var hostname = identity.HostName ?? string.Empty;
        var displayName = new[] { agent.FriendlyName?.Trim(), agent.Name, identity.HostName, $"Agent-{agent.Id.ToString("N")[..8]}" }
            .First(value => !string.IsNullOrWhiteSpace(value))!;
        // Execution environment is a server invocation lane; the enrolled directory does not own it.
        return new(agent.Id, hostname, displayName, agent.Name, agent.Tags, identity.OperatingSystem, identity.Architecture,
            null, online, reason is null, reason);
    }
}

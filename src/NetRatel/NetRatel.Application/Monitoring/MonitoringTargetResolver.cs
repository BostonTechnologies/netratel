using System.Collections.Immutable;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Application.Monitoring;

public static class MonitoringTargetResolver
{
    public static ImmutableArray<Guid> Resolve(int tenantId, MonitoringTargetSelectionDto selection,
        IReadOnlyDictionary<Guid, int> eligibleAgentTenants, IReadOnlyDictionary<Guid, MonitoringGroupDto> groups,
        IReadOnlySet<Guid> authorizedAgentIds)
    {
        if (tenantId <= 0 || !MonitoringContractValidator.TryValidateTargets(selection, out _)) throw new ArgumentException("invalid_target_selection");
        var selected = new HashSet<Guid>();
        if (selection.Mode == MonitoringTargetMode.AllEligible)
        {
            foreach (var (agent, tenant) in eligibleAgentTenants)
                if (tenant == tenantId) Add(agent);
        }
        else
        {
            foreach (var agent in selection.AgentIds) Add(agent);
            foreach (var groupId in selection.GroupIds)
            {
                if (!groups.TryGetValue(groupId, out var group) || group.GroupId != groupId || group.TenantId != tenantId ||
                    !MonitoringContractValidator.TryValidateGroup(group, out _))
                    throw new ArgumentException("unknown_or_foreign_group");
                foreach (var agent in group.AgentIds) Add(agent);
            }
        }
        if (selected.Count == 0) throw new ArgumentException("no_eligible_targets");
        return selected.Order().ToImmutableArray();

        void Add(Guid agent)
        {
            if (agent == Guid.Empty || !eligibleAgentTenants.TryGetValue(agent, out var tenant) || tenant != tenantId)
                throw new ArgumentException("unknown_or_foreign_agent");
            if (!authorizedAgentIds.Contains(agent)) throw new UnauthorizedAccessException("target_authority_required");
            selected.Add(agent);
            if (selected.Count > MonitoringLimits.MaximumTargetClients) throw new ArgumentException("target_limit_exceeded");
        }
    }
}

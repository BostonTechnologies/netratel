using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Infrastructure.Services;

public sealed class JobTargetResolver(OrchestratorDbContext db, IJobNodeDirectory directory) : IJobTargetResolver
{
    public async Task<JobTargetResolution> ResolveAsync(int tenantId, JobTargetDefinition target, ClientEnvironment? environment,
        IReadOnlyDictionary<string, string[]>? parameters, JobRuntimeTargetInput? runtimeTarget,
        IReadOnlySet<Guid>? authorizedAgentIds, CancellationToken cancellationToken)
    {
        if (tenantId <= 0 || target is null) throw Error("target_required", "A tenant and exactly one complete target source are required.");
        try { target.Validate(); } catch (ArgumentException) { throw Error("target_definition_invalid", "Exactly one complete supported target source is required."); }
        if (environment is { } lane && !Enum.IsDefined(lane)) throw Error("target_environment_invalid", "The selected execution environment is unsupported.");
        if (target.Source != JobTargetSource.RuntimeInput && runtimeTarget is not null)
            throw Error("target_override_conflict", "Caller target values are accepted only by RuntimeInput jobs.");
        await using var transaction = target.Source == JobTargetSource.ExecutionGroup
            ? await JobDefinitionMutationFence.BeginAsync(db, cancellationToken).ConfigureAwait(false) : null;
        IReadOnlyList<JobNodeInventoryDto> selected;
        long? revision = null; string? normalized = null; string? groupName = null;
        var bindings = new List<JobTargetBindingProvenance>();
        switch (target.Source)
        {
            case JobTargetSource.ExplicitNodes:
                selected = await RequireExplicitAsync(target.AgentIds!, cancellationToken).ConfigureAwait(false);
                break;
            case JobTargetSource.ExecutionGroup:
                var groupQuery = db.Database.IsRelational()
                    ? db.ExecutionGroups.FromSqlInterpolated($"SELECT * FROM \"ExecutionGroups\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {target.ExecutionGroupId!.Value} FOR SHARE")
                    : db.ExecutionGroups.Where(x => x.TenantId == tenantId && x.Id == target.ExecutionGroupId);
                var group = await groupQuery.AsNoTracking()
                    .Select(x => new { x.Id, x.Name, x.Enabled, x.Archived, x.Revision }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (group is null || !group.Enabled || group.Archived) throw Error("target_group_unavailable", "The selected HA Group is missing, disabled or archived.");
                var memberIds = await db.ExecutionGroupMembers.AsNoTracking().Where(x => x.TenantId == tenantId && x.GroupId == group.Id)
                    .OrderBy(x => x.AgentId).Select(x => x.AgentId).Take(JobNodeFilterEvaluator.MaximumTargets + 1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                if (memberIds.Length > JobNodeFilterEvaluator.MaximumTargets) throw Error("target_result_capacity_exceeded", "The HA Group exceeds 10000 workers.");
                if (memberIds.Length == 0) throw Error("target_group_empty", "The selected HA Group has no members.");
                // Membership never grants execution authority. Ineligible workers do not take
                // a healthy authorized survivor out of service.
                selected = await directory.ReadAsync(tenantId, memberIds.ToHashSet(), false, cancellationToken).ConfigureAwait(false);
                if (authorizedAgentIds is not null) selected = selected.Where(node => authorizedAgentIds.Contains(node.AgentId)).ToArray();
                revision = group.Revision;
                groupName = group.Name;
                break;
            case JobTargetSource.NodeFilter:
                var expression = JobNodeFilterParser.Parse(target.Filter);
                normalized = JobNodeFilterParser.Format(expression);
                bindings.AddRange(JobNodeFilterParser.ParameterNames(expression).Select(name => new JobTargetBindingProvenance(name, "parameter")));
                selected = JobNodeFilterEvaluator.Resolve(JobNodeFilterParser.Bind(expression, parameters),
                    await directory.ReadAsync(tenantId, authorizedAgentIds, false, cancellationToken).ConfigureAwait(false), cancellationToken);
                break;
            case JobTargetSource.RuntimeInput:
                var schema = target.RuntimeInput!;
                if (runtimeTarget is not null && (runtimeTarget.Kind != schema.Kind || runtimeTarget.AllOf != schema.AllOf))
                    throw Error("target_runtime_type_conflict", "The runtime target kind and all-of setting must match the declared input schema.");
                var values = runtimeTarget?.Values ?? schema.DefaultValues;
                if (values is null || values.Count == 0 || values.Count > JobNodeFilterEvaluator.MaximumTargets ||
                    values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > JobNodeFilterParser.MaximumLiteralLength))
                    throw Error("target_runtime_required", "The declared runtime target requires bounded literal values.");
                if (schema.Kind is JobRuntimeTargetKind.ClientId or JobRuntimeTargetKind.Hostname or JobRuntimeTargetKind.Tag && values.Count != 1)
                    throw Error("target_runtime_cardinality", "A singular typed target input requires exactly one value.");
                var literals = values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                bindings.Add(new(schema.Name, runtimeTarget is null ? "default" : "caller"));
                if (schema.Kind is JobRuntimeTargetKind.ClientId or JobRuntimeTargetKind.ClientIds)
                {
                    var ids = new List<Guid>();
                    foreach (var literal in literals)
                    {
                        if (!Guid.TryParse(literal, out var id) || id == Guid.Empty) throw Error("target_runtime_client_id_invalid", "ClientId inputs require stable UUID values.");
                        ids.Add(id);
                    }
                    selected = await RequireExplicitAsync(ids, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var inventory = await directory.ReadAsync(tenantId, authorizedAgentIds, false, cancellationToken).ConfigureAwait(false);
                    if (schema.Kind is JobRuntimeTargetKind.Hostname or JobRuntimeTargetKind.Hostnames)
                    {
                        var byId = new Dictionary<Guid, JobNodeInventoryDto>();
                        foreach (var name in literals)
                        {
                            var matches = inventory.Where(node => string.Equals(node.Hostname, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                            if (matches.Length == 0) throw Error("target_runtime_no_matches", "Every runtime hostname must identify an authorized client.");
                            if (matches.Length > 1) throw Error("target_name_ambiguous", "A runtime hostname identifies multiple clients; use stable ClientIds.");
                            byId.TryAdd(matches[0].AgentId, matches[0]);
                        }
                        selected = byId.Values.ToArray();
                    }
                    else selected = inventory.Where(node => schema.AllOf
                        ? literals.All(tag => node.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        : literals.Any(tag => node.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))).ToArray();
                }
                break;
            default: throw Error("target_source_unsupported", "The target source is unsupported.");
        }
        if (selected.Count == 0) throw Error("target_no_matches", "The target resolves no authorized clients.");
        if (selected.Count > JobNodeFilterEvaluator.MaximumTargets) throw Error("target_result_capacity_exceeded", "The complete target set exceeds 10000 nodes; narrow the source.");
        var nodes = selected.DistinctBy(x => x.AgentId).OrderBy(x => JobTargetNode.SortKey(x.Hostname, x.Name, x.DisplayName), StringComparer.OrdinalIgnoreCase).ThenBy(x => x.AgentId)
            .Select(node => new JobTargetNode(node.AgentId, node.Hostname, node.DisplayName, node.Tags)
            {
                Name = node.Name, OperatingSystem = node.OperatingSystem, Architecture = node.Architecture,
                Environment = environment, IsOnline = node.IsOnline, IsEnabled = node.IsEnabled
            }).ToArray();
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(nodes, revision, normalized, bindings) { GroupName = groupName };

        async Task<IReadOnlyList<JobNodeInventoryDto>> RequireExplicitAsync(IReadOnlyList<Guid> requested, CancellationToken ct)
        {
            var ids = requested.Distinct().ToHashSet();
            if (ids.Count is 0 or > JobNodeFilterEvaluator.MaximumTargets || ids.Contains(Guid.Empty))
                throw Error("target_nodes_invalid", "The complete target set requires 1–10000 valid client IDs.");
            if (authorizedAgentIds is not null && ids.Any(id => !authorizedAgentIds.Contains(id)))
                throw Error("target_authority_required", "The complete target set is not authorized.", 403);
            var found = await directory.ReadAsync(tenantId, ids, true, ct).ConfigureAwait(false);
            if (found.Count != ids.Count || found.Any(node => !node.IsEnabled))
                throw Error("target_node_unavailable", "Every explicit or group member must be a current enabled client in the tenant.");
            return found;
        }
    }
    private static JobTargetValidationException Error(string code, string message, int status = 400) => new(code, message, status);
}

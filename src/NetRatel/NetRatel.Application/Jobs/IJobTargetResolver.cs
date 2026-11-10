using NetRatel.Shared;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Application.Jobs;

public sealed record JobTargetResolution(IReadOnlyList<JobTargetNode> Nodes, long? GroupRevision,
    string? NormalizedFilter, IReadOnlyList<JobTargetBindingProvenance> Bindings);

/// <summary>Resolves the entire set before pagination. The caller rechecks execution authority before dispatch.</summary>
public interface IJobTargetResolver
{
    Task<JobTargetResolution> ResolveAsync(int tenantId, JobTargetDefinition target, ClientEnvironment? environment,
        IReadOnlyDictionary<string, string[]>? parameters, JobRuntimeTargetInput? runtimeTarget,
        IReadOnlySet<Guid>? authorizedAgentIds, CancellationToken cancellationToken);
}

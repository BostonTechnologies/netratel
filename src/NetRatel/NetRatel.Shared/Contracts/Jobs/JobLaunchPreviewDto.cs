namespace NetRatel.Shared.Contracts.Jobs;

/// <summary>A page from a complete canonical launch plan. Digest always binds the entire authorized target set.</summary>
public sealed record JobLaunchPreviewDto(string Digest, string DefinitionDigest, int TargetCount,
    JobTargetDefinition Target, JobExecutionPolicy ExecutionPolicy, int EffectiveConcurrency, long? GroupRevision,
    IReadOnlyList<JobTargetBindingProvenance> Bindings, IReadOnlyList<JobTargetNode> Nodes,
    int Page, int PageSize, int? NextPage);

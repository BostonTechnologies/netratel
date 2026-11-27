using NetRatel.Shared;

namespace NetRatel.Shared.Contracts.Jobs;

public sealed record JobShellPreviewRequest(JobTargetDefinition? Target, JobExecutionPolicy? ExecutionPolicy,
    ClientEnvironment? Environment, JobShellStepDraft? Step = null);

public sealed record JobShellStepDraft(ulong? Id, JobStepTypeDto Type, string? Runner, ulong? ScriptId,
    string? PayloadJson, bool Enabled);

public sealed record JobShellPreviewDto(bool Allowed, bool Verified, string? Message = null);

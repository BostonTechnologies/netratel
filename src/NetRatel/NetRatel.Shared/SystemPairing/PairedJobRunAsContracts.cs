using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Shared.SystemPairing;

public sealed record PairedJobExecutionReadiness(bool Ready, string Code, string Message);

public sealed record PairedJobRunAsTarget(Guid AgentId, string Node, Guid? ProfileId,
    ExecutionIdentityMode? Mode, long? ProfileRevision);

public sealed record PairedJobRunAsReview(PairedJobExecutionReadiness Readiness, long MappingRevision,
    IReadOnlyList<PairedJobRunAsTarget> Targets, string? ScopeDigest, Guid? ApprovalId);

public sealed record ApprovePairedJobRunAsRequest(string ScopeDigest);

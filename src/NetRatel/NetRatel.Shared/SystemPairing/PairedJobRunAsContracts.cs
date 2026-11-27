using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Shared.SystemPairing;

public sealed record PairedJobExecutionReadiness(bool Ready, string Code, string Message)
{
    public string? Reason { get; init; }
}

public static class PairedRunAsReasons
{
    public const string ApprovalRequired = "approval_required";
    public const string ApprovalRevoked = "approval_revoked";
    public const string ScopeChanged = "approval_scope_changed";
    public const string ConnectionUnavailable = "connection_authority_unavailable";
    public const string OperatorDenied = "run_as_approval_denied";
}

public sealed record PairedJobRunAsTarget(Guid AgentId, string Node, Guid? ProfileId,
    ExecutionIdentityMode? Mode, long? ProfileRevision)
{
    public string? ProfileName { get; init; }
}

public sealed record PairedJobRunAsReview(PairedJobExecutionReadiness Readiness, long MappingRevision,
    IReadOnlyList<PairedJobRunAsTarget> Targets, string? ScopeDigest, Guid? ApprovalId);

public sealed record ApprovePairedJobRunAsRequest(string ScopeDigest);

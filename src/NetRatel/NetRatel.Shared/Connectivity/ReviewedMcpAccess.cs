using System.Text.Json.Serialization;

namespace NetRatel.Shared.Connectivity;

/// <summary>One exact reviewed business operation. Agent targets require the persisted profile revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewedMcpAccessTarget(int TenantId, string Tool, string Operation, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? AgentId = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ProfileRevision = null);

/// <summary>Immutable proposal; requestId is the caller's durable idempotency key, never a bearer capability.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewedMcpAccessProposal(string RequestId, string Name, string Action, DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<IntegrationPairingGrant> Grants, IReadOnlyList<string> InstancePermissions,
    IReadOnlyList<ReviewedMcpAccessTarget> Targets, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CredentialId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewedMcpAccessReference(string RequestId);

public sealed record ReviewedMcpAccessRequest(string RequestId, string Status, string VerificationUri, string? UserCode,
    ReviewedMcpAccessProposal Proposal, string Resource, string Instance, string Environment,
    string RequestingPrincipalId, string RequestingCredentialId, long Revision, string ProposalHash)
{
    public string? CredentialRevisionHash { get; init; }
    public string PolicyRevisionHash { get; init; } = "";
    public override string ToString() => $"Reviewed access request {RequestId} {Status} (code redacted)";
}

public sealed record ReviewedMcpAccessAdmission(ReviewedMcpAccessTarget Target, string RequiredScope,
    IReadOnlyList<string> RequiredRoles, bool ApplicationGrantConfigured, bool TenantVisible, bool? PolicyAllowed,
    string? FailureCode, IReadOnlyList<Guid> MatchingPolicies)
{
    public string PolicyAdmissionStatus { get; init; } = "evaluated";
    public bool OwnerPermissionGranted { get; init; }
    public bool AuthorizationAdmitted { get; init; }
    public string TargetReadiness { get; init; } = "not_checked";
    public string CapabilityAvailability { get; init; } = "not_checked";
    public string AdmissionKind { get; init; } = "authorization_only_dispatch_rechecks_runtime";
}

/// <summary>Safe configured, delegated and admitted facts are deliberately separate. No delivery material belongs here.</summary>
public sealed record ReviewedMcpAccessReceipt(ReviewedMcpAccessRequest Request, string? CredentialId,
    DateTimeOffset? CredentialExpiresAtUtc, string CredentialState, IReadOnlyList<IntegrationPairingGrant> ConfiguredGrants,
    IReadOnlyList<string> ConfiguredInstancePermissions, IReadOnlyList<ReviewedMcpAccessAdmission> EffectiveAdmission,
    IReadOnlyList<string> RemainingPrerequisites, string NextAction);

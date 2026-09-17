using NetRatel.Application.Operations;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Persisted general MCP operator policy. Constraints are stored as a
/// source-generated System.Text.Json document because the constraint set is
/// intentionally extensible while selector, effect, target and operation
/// fields remain individually indexed and queryable.
/// </summary>
public sealed class McpOperatorPolicyRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public McpOperatorEnvironment Environment { get; set; }
    public McpOperatorPolicyEffect Effect { get; set; }
    public int Priority { get; set; }
    public McpOperatorPrincipalSelectorKind PrincipalSelectorKind { get; set; }
    public string PrincipalSelectorValue { get; set; } = string.Empty;
    public McpOperatorTargetSelectorKind TargetSelectorKind { get; set; }
    public int TenantId { get; set; }
    public Guid? AgentId { get; set; }
    public string? ClientTag { get; set; }
    public McpOperatorTargetClassification? TargetClassification { get; set; }
    public McpOperatorOperationFamily OperationFamily { get; set; }
    public string? Operation { get; set; }
    public string ConstraintsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset? ReviewByUtc { get; set; }
    public DateTimeOffset? DisabledAtUtc { get; set; }
    public string? DisabledBy { get; set; }
    public McpOperatorPolicyLifecycleState LifecycleState { get; set; } = McpOperatorPolicyLifecycleState.Active;
    public long Version { get; set; }
    public string? AuditReference { get; set; }
}

/// <summary>
/// Persisted server-owned target facts used by MCP policy evaluation. Tags are
/// stored as a bounded JSON array and only become authorization-relevant when
/// a policy explicitly selects one of them.
/// </summary>
public sealed class McpOperatorTargetProfileRecord
{
    public Guid AgentId { get; set; }
    public int TenantId { get; set; }
    public McpOperatorTargetClassification Classification { get; set; }
    public string TagsJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public long Version { get; set; }
}

/// <summary>
/// Append-only evidence for policy and target-profile administration. It does
/// not duplicate policy constraints or any operator request content.
/// </summary>
public sealed class McpOperatorPolicyChangeAuditRecord
{
    public Guid Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid? PolicyId { get; set; }
    public Guid? AgentId { get; set; }
    public int TenantId { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public long Version { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

/// <summary>
/// Append-only accepted-operation audit. It records trusted identities and
/// routing metadata, never raw payload, command, file content, bearer token or
/// secret value.
/// </summary>
public sealed class McpOperatorAcceptedAuditRecord
{
    public Guid Id { get; set; }
    public Guid PolicyId { get; set; }
    public McpOperatorEnvironment Environment { get; set; }
    public string ServicePrincipal { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string? ClientId { get; set; }
    public string? AuthorizedParty { get; set; }
    public string GroupsJson { get; set; } = "[]";
    public string RolesJson { get; set; } = "[]";
    public string ScopesJson { get; set; } = "[]";
    public string? McpResource { get; set; }
    public string? McpInstance { get; set; }
    public string? Tool { get; set; }
    public int TenantId { get; set; }
    public Guid? AgentId { get; set; }
    public McpOperatorOperationFamily OperationFamily { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}

/// <summary>
/// Short-lived binary content collected through the V2 file operator
/// contract. The remote path is deliberately excluded; access is instead
/// bounded by tenant, target, caller, MCP resource/instance, and the frozen
/// fingerprint of the admitted read roots.
/// </summary>
public sealed class McpOperatorFileArtifactRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string McpResource { get; set; } = string.Empty;
    public string McpInstance { get; set; } = string.Empty;
    public string ReadRootFingerprint { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
    public Guid AcceptedAuditId { get; set; }
    public Guid IdempotencyId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
}

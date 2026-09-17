using NetRatel.Application.Operations;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Persisted, explicit Dev-only target grant. Revoking an older grant preserves
/// the evidence for its original approval while preventing future use.
/// </summary>
public sealed class DevelopmentOperatorTargetGrant
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public DevelopmentOperatorTargetClassification Classification { get; set; }
    public DevelopmentOperatorOperationScope AllowedOperations { get; set; }
    public string? FileFixtureRoot { get; set; }
    public string EvidenceReference { get; set; } = string.Empty;
    public string GrantedBy { get; set; } = string.Empty;
    public DateTimeOffset GrantedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
    public string? RevocationReason { get; set; }
}

/// <summary>
/// Append-only audit of a request accepted by the server-owned Development
/// target gate. It intentionally contains no command, file, or secret content.
/// </summary>
public sealed class DevelopmentOperatorAcceptedAuditRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid TargetGrantId { get; set; }
    public DevelopmentOperatorOperation Operation { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}

/// <summary>
/// Bounded, short-lived content collected from a marker-owned Development file
/// fixture. The remote source path is deliberately not retained.
/// </summary>
public sealed class DevelopmentMcpFileArtifactRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid TargetGrantId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool MarkerOwned { get; set; }
    public byte[] Content { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
}

/// <summary>
/// Ownership boundary for a marker script created through the Development MCP
/// adapter. Script-library entries are otherwise global, so this record is the
/// durable proof that a script belongs only to one approved QA target.
/// </summary>
public sealed class DevelopmentMcpScriptRecord
{
    public Guid Id { get; set; }
    public long ScriptId { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid TargetGrantId { get; set; }
    public string Marker { get; set; } = string.Empty;
    public string Shell { get; set; } = string.Empty;
    public string ExecutionMode { get; set; } = "standard";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
}

/// <summary>
/// Durable ownership boundary for the one fixed-library-script job that may be
/// created from a Development MCP marker script. The global job catalog is not
/// an MCP surface; this record prevents the adapter from starting, cancelling,
/// or deleting any job it did not create for the approved target.
/// </summary>
public sealed class DevelopmentMcpMarkerJobRecord
{
    public Guid Id { get; set; }
    public long JobId { get; set; }
    public long ScriptId { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid TargetGrantId { get; set; }
    public string Marker { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
}

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Caller-owned, policy-frozen V2 projection over a domain request. Request
/// inputs, unbounded history, and unredacted result bytes are intentionally
/// absent from this record.
/// </summary>
public sealed class McpOperatorRequestRecord
{
    public Guid Id { get; set; }
    public int RequestId { get; set; }
    public long JobId { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string McpResource { get; set; } = string.Empty;
    public string McpInstance { get; set; } = string.Empty;
    public Guid PolicyId { get; set; }
    public long PolicyVersion { get; set; }
    public string TargetSetDigest { get; set; } = string.Empty;
    public Guid AcceptedAuditId { get; set; }
    public Guid IdempotencyId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string State { get; set; } = "Pending";
    public string Summary { get; set; } = string.Empty;
    public string? ResultSummary { get; set; }
    public string? ClaimReferenceHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long Version { get; set; }
}

/// <summary>Append-only evidence for each admitted V2 request lifecycle step.</summary>
public sealed class McpOperatorRequestAuditRecord
{
    public Guid Id { get; set; }
    public Guid RequestRecordId { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid AcceptedAuditId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

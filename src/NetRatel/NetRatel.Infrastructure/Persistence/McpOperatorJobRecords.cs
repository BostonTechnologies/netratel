using NetRatel.Application.Operations;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Explicit ownership, policy, target-set, and concurrency state for a
/// Production operator job. The linked Jobs row remains compatible with the
/// existing job authority but is never exposed through this surface without
/// this record.
/// </summary>
public sealed class McpOperatorJobRecord
{
    public Guid Id { get; set; }
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
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public long Version { get; set; }
}

/// <summary>
/// Append-only evidence for an admitted change to an operator job definition.
/// It intentionally stores the action and revision only: job payloads remain
/// in typed tables and no secret parameter values are duplicated here.
/// </summary>
public sealed class McpOperatorJobAuditRecord
{
    public Guid Id { get; set; }
    public Guid JobRecordId { get; set; }
    public long JobId { get; set; }
    public long JobVersion { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid AcceptedAuditId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

/// <summary>
/// Durable owner-scoped run lease. It preserves the exact admitted target
/// digest and correlation for cancellation, lookup, and retention decisions
/// without altering the shared job-run projection used by the gateway.
/// </summary>
public sealed class McpOperatorJobRunRecord
{
    public Guid Id { get; set; }
    public long JobRunId { get; set; }
    public Guid JobRecordId { get; set; }
    public long JobId { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid AcceptedAuditId { get; set; }
    public Guid? IdempotencyId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string TargetSetDigest { get; set; } = string.Empty;
    public bool CancellationRequested { get; set; }
    public DateTimeOffset? CancellationRequestedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Append-only accepted-operation evidence for a job-run lifecycle change.</summary>
public sealed class McpOperatorJobRunAuditRecord
{
    public Guid Id { get; set; }
    public Guid JobRunRecordId { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid AcceptedAuditId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

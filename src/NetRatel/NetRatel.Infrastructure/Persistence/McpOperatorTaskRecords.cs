namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Explicit ownership, frozen policy context, and bounded result summary for
/// a one-shot Production task. Command text and script content are deliberately
/// excluded; the existing activity row continues to carry gateway lifecycle.
/// </summary>
public sealed class McpOperatorTaskRecord
{
    public Guid Id { get; set; }
    public long TaskActivityId { get; set; }
    public string CommandId { get; set; } = string.Empty;
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
    public string TaskType { get; set; } = string.Empty;
    public string? ShellType { get; set; }
    public string? CommandHash { get; set; }
    public int CommandLength { get; set; }
    public long? ScriptId { get; set; }
    public long? ScriptVersion { get; set; }
    public string? ScriptContentHash { get; set; }
    public int TimeoutSeconds { get; set; }
    public int MaximumOutputBytes { get; set; }
    public string State { get; set; } = "Pending";
    public string? ResultSummary { get; set; }
    public bool CancellationRequested { get; set; }
    public DateTimeOffset? CancellationRequestedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long Version { get; set; }
}

/// <summary>Append-only content-free evidence for an admitted task change.</summary>
public sealed class McpOperatorTaskAuditRecord
{
    public Guid Id { get; set; }
    public Guid TaskRecordId { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid AcceptedAuditId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

using NetRatel.Application.Operations;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Durable, content-free ownership lease for a Production operator terminal.
/// The agent owns the PTY and all terminal bytes remain transient; this row
/// exists so API restart/reconnect cannot turn a retained PTY into an orphan.
/// </summary>
public sealed class McpOperatorTerminalSessionRecord
{
    public Guid Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public ulong Generation { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string McpResource { get; set; } = string.Empty;
    public string McpInstance { get; set; } = string.Empty;
    public Guid PolicyId { get; set; }
    public long PolicyVersion { get; set; }
    public Guid AcceptedAuditId { get; set; }
    public Guid? IdempotencyId { get; set; }
    public string ShellType { get; set; } = string.Empty;
    public string? WorkingDirectory { get; set; }
    public int Columns { get; set; }
    public int Rows { get; set; }
    public string EffectiveConstraintsJson { get; set; } = "{}";
    public McpOperatorTerminalSessionState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastActivityAtUtc { get; set; }
    public DateTimeOffset IdleExpiresAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? CloseRequestedAtUtc { get; set; }
    public string? CloseReason { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public string? FailureCode { get; set; }
    public long Version { get; set; }
}

/// <summary>
/// Append-only lifecycle evidence for a terminal lease. It records transition
/// metadata only and never terminal bytes, commands, working-directory input,
/// or secret-bearing environment values.
/// </summary>
public sealed class McpOperatorTerminalSessionAuditRecord
{
    public Guid Id { get; set; }
    public Guid SessionRecordId { get; set; }
    public McpOperatorTerminalSessionState State { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

/// <summary>
/// Durable, content-free idempotency state for one control frame sent to an
/// already owned terminal session. The payload hash is sufficient to reject
/// a changed replay without persisting terminal input bytes.
/// </summary>
public sealed class McpOperatorTerminalActionRecord
{
    public Guid Id { get; set; }
    public Guid SessionRecordId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string DelegationRequestId { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public McpOperatorIdempotencyOutcome Outcome { get; set; }
    public string? ResultReference { get; set; }
    public Guid? AcceptedAuditId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long Version { get; set; }
}

/// <summary>
/// Durable command ownership and frozen-policy evidence. Raw command text,
/// and environment values are never persisted here. Terminal output is redacted
/// and bounded before storage.
/// </summary>
public sealed class McpOperatorCommandRecord
{
    public Guid Id { get; set; }
    public string CommandId { get; set; } = string.Empty;
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string McpResource { get; set; } = string.Empty;
    public string McpInstance { get; set; } = string.Empty;
    public Guid PolicyId { get; set; }
    public long PolicyVersion { get; set; }
    public Guid AcceptedAuditId { get; set; }
    public Guid? IdempotencyId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string ShellType { get; set; } = string.Empty;
    public string? WorkingDirectory { get; set; }
    public string CommandHash { get; set; } = string.Empty;
    public int CommandLength { get; set; }
    public string EnvironmentReferencesJson { get; set; } = "[]";
    public int TimeoutSeconds { get; set; }
    public int MaximumOutputBytes { get; set; }
    public string EffectiveConstraintsJson { get; set; } = "{}";
    public McpOperatorCommandState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastUpdatedAtUtc { get; set; }
    public string? FailureCode { get; set; }
    public string? OutputJson { get; set; }
    public long Version { get; set; }
}

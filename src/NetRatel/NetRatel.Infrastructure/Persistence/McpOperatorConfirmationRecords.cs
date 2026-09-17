using NetRatel.Application.Operations;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Server-side confirmation-plan state. Only the hash of the opaque plan token
/// and canonical payload are persisted; no mutation payload is retained here.
/// </summary>
public sealed class McpOperatorConfirmationPlanRecord
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public McpOperatorEnvironment Environment { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string? McpResource { get; set; }
    public string? McpInstance { get; set; }
    public int TenantId { get; set; }
    public Guid? AgentId { get; set; }
    public string TargetSetDigest { get; set; } = string.Empty;
    public Guid PolicyId { get; set; }
    public long PolicyVersion { get; set; }
    public McpOperatorOperationFamily OperationFamily { get; set; }
    public string Operation { get; set; } = string.Empty;
    public McpOperatorConfirmationClass ConfirmationClass { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
    public Guid? ConsumedIdempotencyId { get; set; }
    public long Version { get; set; }
}

/// <summary>
/// One durable dispatch admission key. The result reference is an opaque ID or
/// state locator; operation output and request content remain elsewhere.
/// </summary>
public sealed class McpOperatorIdempotencyRecord
{
    public Guid Id { get; set; }
    public McpOperatorEnvironment Environment { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public int TenantId { get; set; }
    public Guid? AgentId { get; set; }
    public string TargetSetDigest { get; set; } = string.Empty;
    public Guid PolicyId { get; set; }
    public long PolicyVersion { get; set; }
    public McpOperatorOperationFamily OperationFamily { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public McpOperatorIdempotencyOutcome Outcome { get; set; }
    public string? ResultReference { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long Version { get; set; }
}

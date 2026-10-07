namespace NetRatel.Infrastructure.Persistence;

public sealed class AgentRefreshToken
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }
    public DateTimeOffset? RecoveryUsedAtUtc { get; set; }
    // The consumed parent owns one recoverable immediate successor. Only the successor
    // secret is protected; the remaining fields bind and reject obsolete decisions.
    public Guid? ExchangeId { get; set; }
    public int? ExchangeTenantId { get; set; }
    public string? ExchangeKeyHash { get; set; }
    public string? ExchangeRequestedScopesJson { get; set; }
    public string? ExchangeGrantedScopesJson { get; set; }
    public string? ExchangeMtlsThumbprint { get; set; }
    public string? ProtectedSuccessorToken { get; set; }
    public DateTimeOffset? ExchangeAcknowledgedAtUtc { get; set; }

    public Agent Agent { get; set; } = null!;
}

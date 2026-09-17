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

    public Agent Agent { get; set; } = null!;
}

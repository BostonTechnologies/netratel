namespace NetRatel.Infrastructure.Persistence;

public sealed class AgentCredential
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string RefreshTokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }

    public Agent Agent { get; set; } = null!;
}

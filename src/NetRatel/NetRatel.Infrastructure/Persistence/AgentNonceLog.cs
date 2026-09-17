namespace NetRatel.Infrastructure.Persistence;

public sealed class AgentNonceLog
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

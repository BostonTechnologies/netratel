namespace NetRatel.Infrastructure.Persistence;

public sealed class AgentTokenEvent
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public string? DetailsJson { get; set; }
}

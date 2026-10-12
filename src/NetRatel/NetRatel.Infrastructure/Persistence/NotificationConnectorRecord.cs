namespace NetRatel.Infrastructure.Persistence;

public sealed class NotificationConnectorRecord
{
    public int TenantId { get; set; }
    public Guid Id { get; set; }
    public long Revision { get; set; }
    public string OwnerPrincipalId { get; set; } = string.Empty;
    public string ConfigurationJson { get; set; } = string.Empty;
    public string ProtectedWebhookUrl { get; set; } = string.Empty;
    public DateTimeOffset? DeletedAtUtc { get; set; }
}

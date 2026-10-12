namespace NetRatel.Shared.Contracts.Notifications;

public enum NotificationProvider { Discord = 1, Slack = 2 }
public enum NotificationDeliveryStatus { Succeeded, Rejected, RateLimited, DeliveryUnknown, Unavailable }

public sealed record NotificationConnectorConfiguration(string Name, NotificationProvider Provider, bool Enabled,
    string? DiscordUsername = null, string? DiscordThreadId = null);

/// <summary>The webhook credential is write-only and is never returned to the browser or flow graph.</summary>
public sealed record NotificationConnectorDto(Guid Id, int TenantId, long Revision,
    NotificationConnectorConfiguration Configuration, bool HasWebhook);
public sealed record NotificationConnectorTenantDto(int TenantId, string Name);
public sealed record SaveNotificationConnectorRequest(long ExpectedRevision,
    NotificationConnectorConfiguration Configuration, string? WebhookUrl = null)
{
    public override string ToString() => $"Notification connector configuration, revision {ExpectedRevision}";
}
public sealed record NotificationConnectorRevisionRequest(long ExpectedRevision);
public sealed record NotificationDeliveryResult(NotificationDeliveryStatus Status, string Code,
    string? MessageId = null, TimeSpan? RetryAfter = null);

public static class NotificationConnectorLimits
{
    public const int MaximumConnectorsPerTenant = 64;
    public const int MaximumNameLength = 128;
    public const int MaximumWebhookLength = 2048;
    public const int MaximumMessageLength = 1800;
    public const int MaximumClientListLength = 1000;
}

using System.Security.Claims;
using NetRatel.Shared.Contracts.Notifications;

namespace NetRatel.Application.Notifications;

public sealed record NotificationConnectorState(Guid Id, int TenantId, long Revision, string OwnerPrincipalId,
    NotificationConnectorConfiguration Configuration, string ProtectedWebhookUrl, DateTimeOffset? DeletedAtUtc = null)
{
    public override string ToString() => $"Notification connector {Id:D}, tenant {TenantId}, revision {Revision}";
}

public interface INotificationConnectorStore
{
    Task<NotificationConnectorState?> GetAsync(int tenantId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<NotificationConnectorState>> ListAsync(int tenantId, CancellationToken ct = default);
    Task<bool> SaveAsync(NotificationConnectorState state, long expectedRevision, CancellationToken ct = default);
}

public interface INotificationCredentialProtector
{
    string Protect(int tenantId, Guid connectorId, string webhookUrl);
    string Unprotect(int tenantId, Guid connectorId, string ciphertext);
}

public interface INotificationConnectorService
{
    Task<IReadOnlyList<NotificationConnectorDto>> ListAsync(int tenantId, ClaimsPrincipal actor, CancellationToken ct = default);
    Task<NotificationConnectorDto> SaveAsync(int tenantId, Guid id, SaveNotificationConnectorRequest request,
        ClaimsPrincipal actor, CancellationToken ct = default);
    Task DeleteAsync(int tenantId, Guid id, long expectedRevision, ClaimsPrincipal actor, CancellationToken ct = default);
    Task<NotificationDeliveryResult> SendTestAsync(int tenantId, Guid id, ClaimsPrincipal actor, CancellationToken ct = default);
}

public interface INotificationWebhookTransport
{
    Task<NotificationDeliveryResult> SendAsync(NotificationProvider provider, string webhookUrl, string message,
        string? discordUsername, string? discordThreadId, CancellationToken ct = default);
}

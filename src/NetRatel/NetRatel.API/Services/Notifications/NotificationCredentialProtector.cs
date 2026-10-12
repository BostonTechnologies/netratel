using Microsoft.AspNetCore.DataProtection;
using NetRatel.Application.Notifications;

namespace NetRatel.API.Services.Notifications;

public sealed class NotificationCredentialProtector(IDataProtectionProvider provider) : INotificationCredentialProtector
{
    private IDataProtector For(int tenantId, Guid connectorId) => provider.CreateProtector("NetRatel.NotificationWebhook.v1",
        tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture), connectorId.ToString("D"));
    public string Protect(int tenantId, Guid connectorId, string webhookUrl) => For(tenantId, connectorId).Protect(webhookUrl);
    public string Unprotect(int tenantId, Guid connectorId, string ciphertext) => For(tenantId, connectorId).Unprotect(ciphertext);
}

using Microsoft.AspNetCore.DataProtection;
using NetRatel.Application.RatelDesk;

namespace NetRatel.API.Services.RatelDesk;

/// <summary>Uses the API's existing persisted key ring, binding ciphertext to the tenant and connector.</summary>
public sealed class RatelDeskCredentialProtector(IDataProtectionProvider protection) : IRatelDeskCredentialProtector
{
    private IDataProtector For(int tenantId, Guid connectorId) => protection.CreateProtector(
        "NetRatel.RatelDesk.Credential.v1", tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture), connectorId.ToString("D"));
    public string Protect(int tenantId, Guid connectorId, string credential) => For(tenantId, connectorId).Protect(credential);
    public string Unprotect(int tenantId, Guid connectorId, string ciphertext) => For(tenantId, connectorId).Unprotect(ciphertext);
}

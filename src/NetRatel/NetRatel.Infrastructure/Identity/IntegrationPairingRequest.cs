namespace NetRatel.Infrastructure.Identity;

public enum IntegrationPairingState { Pending, Approved, Denied, Expired, Issued, Consumed, Cancelled }

/// <summary>Bounded approval state and protected delivery. Permanent credentials retain only their verifier.</summary>
public sealed class IntegrationPairingRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserCodeHash { get; set; } = string.Empty;
    public string DeviceCapabilityHash { get; set; } = string.Empty;
    public string PublicOrigin { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public IntegrationCredentialPurpose Purpose { get; set; }
    public string? Resource { get; set; }
    public string RequestedPermissionsJson { get; set; } = "[]";
    public IntegrationPairingState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset NextPollAtUtc { get; set; }
    public int PollIntervalSeconds { get; set; } = 5;
    public string? OwnerPrincipalId { get; set; }
    public string? ApprovedGrantsJson { get; set; }
    public string? ApprovedInstancePermissionsJson { get; set; }
    public DateTimeOffset? CredentialExpiresAtUtc { get; set; }
    public string? CredentialId { get; set; }
    public string? ProtectedDelivery { get; set; }
    public DateTimeOffset? DeliveryExpiresAtUtc { get; set; }
    public long Revision { get; set; }
}

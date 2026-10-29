namespace NetRatel.Infrastructure.Identity;

/// <summary>Short protected handoff for an atomic browser setup. No plaintext secret is persisted.</summary>
public sealed class IntegrationSetupAttempt
{
    public string Id { get; set; } = string.Empty;
    public string OwnerPrincipalId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string CredentialId { get; set; } = string.Empty;
    public string? ProtectedDelivery { get; set; }
    public DateTimeOffset DeliveryExpiresAtUtc { get; set; }
    public bool Acknowledged { get; set; }
}

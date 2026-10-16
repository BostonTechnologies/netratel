namespace NetRatel.Infrastructure.Identity;

/// <summary>
/// Non-authenticating lifecycle history. Deliberately has no foreign key to the
/// live credential or principal, so deleting a credential preserves attribution.
/// </summary>
public sealed class IntegrationCredentialLifecycleEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CredentialId { get; set; } = string.Empty;
    public string OwnerPrincipalId { get; set; } = string.Empty;
    public string ActorPrincipalId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}

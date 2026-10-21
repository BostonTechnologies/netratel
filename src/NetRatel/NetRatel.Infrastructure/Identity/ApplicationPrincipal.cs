namespace NetRatel.Infrastructure.Identity;

/// <summary>
/// Stable NetRatel identity independent of an authentication provider. An
/// external association is identified only by its verified issuer and subject.
/// </summary>
public sealed class ApplicationPrincipal
{
    public string Id { get; set; } = CreateId();

    public static string CreateId() => Guid.NewGuid().ToString("N");

    public string? LocalUserId { get; set; }

    public string? ExternalIssuer { get; set; }

    public string? ExternalSubject { get; set; }

    // Only the API's validated OIDC identity writes these bounded authority facts.
    // They expire with that token and are re-evaluated against current configuration.
    public string? ExternalAuthorityClaimsJson { get; set; }
    public DateTimeOffset? ExternalAuthorityIssuedAtUtc { get; set; }
    public DateTimeOffset? ExternalAuthorityExpiresAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

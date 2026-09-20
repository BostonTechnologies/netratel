namespace NetRatel.Infrastructure.Identity;

/// <summary>
/// Stable NetRatel identity independent of an authentication provider. An
/// external association is identified only by its verified issuer and subject.
/// </summary>
public sealed class ApplicationPrincipal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string? LocalUserId { get; set; }

    public string? ExternalIssuer { get; set; }

    public string? ExternalSubject { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

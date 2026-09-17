namespace NetRatel.Infrastructure.Persistence;

public sealed class OidcSigningKey
{
    public Guid Id { get; set; }
    public string KeyId { get; set; } = string.Empty;
    public string PrivateKeyPem { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool IsActive { get; set; }
}

namespace NetRatel.Infrastructure.Persistence;

public sealed class SecretRecord
{
    public int Id { get; set; }
    public int? TenantId { get; set; }
    public string? ClientIdentity { get; set; }
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

namespace NetRatel.Infrastructure.Persistence;

public sealed class SecretRecord
{
    public int Id { get; set; }
    public int? TenantId { get; set; }
    public string? ClientIdentity { get; set; }
    public string Value { get; set; } = string.Empty;
    public bool ValueProtected { get; set; }
    public string? Name { get; set; }
    public long Version { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public bool VersionBoundProtection { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class SecretVersionRecord
{
    public int SecretId { get; set; }
    public long Version { get; set; }
    public required string ProtectedValue { get; set; }
}

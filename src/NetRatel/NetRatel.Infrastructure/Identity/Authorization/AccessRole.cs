namespace NetRatel.Infrastructure.Identity.Authorization;

/// <summary>
/// A NetRatel authorization role. Identity framework roles remain available for
/// authentication internals; this type owns durable, tenant-scoped product access.
/// </summary>
public sealed class AccessRole
{
    public string Id { get; set; } = ApplicationPrincipal.CreateId();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsBuiltIn { get; set; }
    public bool IsInstanceAdministratorRole { get; set; }
    public int DelegationRank { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<AccessRolePermission> Permissions { get; } = new List<AccessRolePermission>();
}

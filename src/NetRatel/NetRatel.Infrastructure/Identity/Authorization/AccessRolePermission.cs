namespace NetRatel.Infrastructure.Identity.Authorization;

public sealed class AccessRolePermission
{
    public string RoleId { get; set; } = string.Empty;
    public required string Permission { get; set; }
    public AccessRole? Role { get; set; }
}

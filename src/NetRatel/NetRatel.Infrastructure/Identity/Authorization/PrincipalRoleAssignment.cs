namespace NetRatel.Infrastructure.Identity.Authorization;

/// <summary>
/// Binds one stable application principal to one role at instance scope or a
/// single tenant scope. A null tenant is intentionally instance-wide.
/// </summary>
public sealed class PrincipalRoleAssignment
{
    public string Id { get; set; } = ApplicationPrincipal.CreateId();
    public required string PrincipalId { get; set; }
    public required string RoleId { get; set; }
    public int? TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedByPrincipalId { get; set; }
    public AccessRole? Role { get; set; }
}

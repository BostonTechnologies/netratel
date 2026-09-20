using Microsoft.AspNetCore.Identity;

namespace NetRatel.Infrastructure.Identity;

/// <summary>
/// A deployment-owned interactive account. Authorization is deliberately kept
/// out of this type: P03 evaluates durable grants against <see cref="PrincipalId"/>.
/// </summary>
public sealed class LocalUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;

    public string PrincipalId { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public bool IsInstanceAdministrator { get; set; }

    public DateTimeOffset? DisabledAtUtc { get; set; }

    /// <summary>Invalidates browser sessions and access projections when changed.</summary>
    public long AuthorizationRevision { get; set; }
}

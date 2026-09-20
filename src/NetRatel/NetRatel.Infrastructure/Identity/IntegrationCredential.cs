namespace NetRatel.Infrastructure.Identity;

/// <summary>
/// An opaque, one-time-revealed automation credential owned by an application
/// principal. The bearer secret is deliberately never retained; only its
/// SHA-256 verifier is stored.
/// </summary>
public sealed class IntegrationCredential
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>A non-secret lookup identifier displayed to the owner.</summary>
    public string PublicId { get; set; } = string.Empty;

    /// <summary>Non-secret prefix shown in the management UI and audit output.</summary>
    public string TokenPrefix { get; set; } = string.Empty;

    /// <summary>Hex-encoded SHA-256 verifier of the complete bearer secret.</summary>
    public string SecretHash { get; set; } = string.Empty;

    public string OwnerPrincipalId { get; set; } = string.Empty;

    public IntegrationCredentialPurpose Purpose { get; set; }

    /// <summary>Reserved for the canonical paired HTTP MCP resource in P08.</summary>
    public string? Resource { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public DateTimeOffset? LastUsedAtUtc { get; set; }

    public string? RevokedByPrincipalId { get; set; }

    public List<IntegrationCredentialGrant> Grants { get; set; } = [];
}

/// <summary>Credential trust boundaries are intentionally purpose-separated.</summary>
public enum IntegrationCredentialPurpose
{
    Api = 0,
    HttpMcp = 1
}

/// <summary>A tenant and permission are one grant tuple, never independent sets.</summary>
public sealed class IntegrationCredentialGrant
{
    public string CredentialId { get; set; } = string.Empty;

    public int TenantId { get; set; }

    public string Permission { get; set; } = string.Empty;

    public IntegrationCredential? Credential { get; set; }
}

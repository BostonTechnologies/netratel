// Kept in the original namespace for source and assembly-forwarding compatibility.
namespace NetRatel.Infrastructure.Identity;

/// <summary>Credential trust boundaries are intentionally purpose-separated.</summary>
public enum IntegrationCredentialPurpose
{
    Api = 0,
    HttpMcp = 1
}

public sealed record IntegrationCredentialGrantRequest(int TenantId, string Permission);

public sealed record IntegrationCredentialSummary(
    string Id,
    string PublicId,
    string TokenPrefix,
    string Name,
    IntegrationCredentialPurpose Purpose,
    string? Resource,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    IReadOnlyList<IntegrationCredentialGrantRequest> Grants)
{
    public IReadOnlyList<string> InstancePermissions { get; init; } = [];
}

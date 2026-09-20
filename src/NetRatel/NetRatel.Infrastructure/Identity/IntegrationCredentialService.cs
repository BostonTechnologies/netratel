using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Identity;

public sealed record IntegrationCredentialGrantRequest(int TenantId, string Permission);

public sealed record IntegrationCredentialCreateRequest(
    string Name,
    IntegrationCredentialPurpose Purpose,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<IntegrationCredentialGrantRequest> Grants,
    string? Resource = null);

public sealed record IntegrationCredentialSecret(
    string CredentialId,
    string PublicId,
    string TokenPrefix,
    string Secret,
    DateTimeOffset ExpiresAtUtc);

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
    IReadOnlyList<IntegrationCredentialGrantRequest> Grants);

public sealed record VerifiedIntegrationCredential(
    string CredentialId,
    string OwnerPrincipalId,
    IntegrationCredentialPurpose Purpose,
    IReadOnlyList<IntegrationCredentialGrantRequest> Grants)
{
    public string? Resource { get; init; }
}

/// <summary>
/// Revalidates a previously exchanged credential by non-secret identifier.
/// The API uses this on every local HTTP MCP execution, so revocation,
/// expiration, account disablement, and grant changes take effect immediately.
/// </summary>
public interface IIntegrationCredentialCurrentVerifier
{
    Task<VerifiedIntegrationCredential?> VerifyCurrentAsync(
        string credentialId,
        IntegrationCredentialPurpose purpose,
        CancellationToken cancellationToken = default);
}

public interface IIntegrationCredentialService
{
    Task<IntegrationCredentialSecret> CreateAsync(string ownerPrincipalId, IntegrationCredentialCreateRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntegrationCredentialSummary>> ListAsync(string ownerPrincipalId, CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(string ownerPrincipalId, string credentialId, string revokedByPrincipalId, CancellationToken cancellationToken = default);
    Task<VerifiedIntegrationCredential?> VerifyAsync(string secret, IntegrationCredentialPurpose purpose, CancellationToken cancellationToken = default);
}

/// <summary>Owns opaque credential generation, one-way verification, and revocation.</summary>
public sealed class IntegrationCredentialService(NetRatelIdentityDbContext db) : IIntegrationCredentialService, IIntegrationCredentialCurrentVerifier
{
    public const string ApiTokenPrefix = "nrt_ic_";
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(365);

    public async Task<IntegrationCredentialSecret> CreateAsync(string ownerPrincipalId, IntegrationCredentialCreateRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name?.Trim();
        var resource = request.Resource?.Trim();
        var now = DateTimeOffset.UtcNow;
        var grants = (request.Grants ?? [])
            .Where(grant => grant.TenantId > 0 && !string.IsNullOrWhiteSpace(grant.Permission))
            .Select(grant => new IntegrationCredentialGrantRequest(grant.TenantId, grant.Permission.Trim()))
            .Distinct()
            .ToArray();

        if (string.IsNullOrWhiteSpace(ownerPrincipalId) || string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            !Enum.IsDefined(request.Purpose) ||
            (request.Purpose == IntegrationCredentialPurpose.HttpMcp && string.IsNullOrWhiteSpace(resource)) ||
            request.ExpiresAtUtc <= now || request.ExpiresAtUtc > now.Add(MaximumLifetime) || grants.Length == 0)
        {
            throw new ArgumentException("Credential name, bounded expiry, and at least one tenant permission grant are required.");
        }

        var secret = ApiTokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var publicId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var credential = new IntegrationCredential
        {
            PublicId = publicId,
            TokenPrefix = secret[..Math.Min(secret.Length, ApiTokenPrefix.Length + 8)],
            SecretHash = Hash(secret),
            OwnerPrincipalId = ownerPrincipalId,
            Purpose = request.Purpose,
            Resource = string.IsNullOrWhiteSpace(resource) ? null : resource,
            Name = name,
            ExpiresAtUtc = request.ExpiresAtUtc
        };
        credential.Grants.AddRange(grants.Select(grant => new IntegrationCredentialGrant
        {
            CredentialId = credential.Id,
            TenantId = grant.TenantId,
            Permission = grant.Permission
        }));

        db.IntegrationCredentials.Add(credential);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(credential.Id, credential.PublicId, credential.TokenPrefix, secret, credential.ExpiresAtUtc);
    }

    public async Task<IReadOnlyList<IntegrationCredentialSummary>> ListAsync(string ownerPrincipalId, CancellationToken cancellationToken = default)
    {
        var credentials = await db.IntegrationCredentials.AsNoTracking()
            .Include(credential => credential.Grants)
            .Where(credential => credential.OwnerPrincipalId == ownerPrincipalId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // SQLite cannot order DateTimeOffset values. Fetch this account's
        // small credential set and apply the chronological view in process.
        return credentials.OrderByDescending(credential => credential.CreatedAtUtc).Select(credential => new IntegrationCredentialSummary(
                credential.Id, credential.PublicId, credential.TokenPrefix, credential.Name, credential.Purpose,
                credential.Resource, credential.CreatedAtUtc, credential.ExpiresAtUtc, credential.RevokedAtUtc,
                credential.LastUsedAtUtc,
                credential.Grants.OrderBy(grant => grant.TenantId).ThenBy(grant => grant.Permission)
                    .Select(grant => new IntegrationCredentialGrantRequest(grant.TenantId, grant.Permission)).ToArray()))
            .ToArray();
    }

    public async Task<bool> RevokeAsync(string ownerPrincipalId, string credentialId, string revokedByPrincipalId, CancellationToken cancellationToken = default)
    {
        var credential = await db.IntegrationCredentials.SingleOrDefaultAsync(candidate =>
            candidate.Id == credentialId && candidate.OwnerPrincipalId == ownerPrincipalId, cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return false;
        }

        if (credential.RevokedAtUtc is null)
        {
            credential.RevokedAtUtc = DateTimeOffset.UtcNow;
            credential.RevokedByPrincipalId = revokedByPrincipalId;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<VerifiedIntegrationCredential?> VerifyAsync(string secret, IntegrationCredentialPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secret) || !secret.StartsWith(ApiTokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var credential = await db.IntegrationCredentials
            .Include(candidate => candidate.Grants)
            .SingleOrDefaultAsync(candidate => candidate.SecretHash == Hash(secret) && candidate.Purpose == purpose &&
                candidate.RevokedAtUtc == null, cancellationToken).ConfigureAwait(false);
        // SQLite cannot translate a DateTimeOffset comparison. The unique
        // verifier lookup remains in SQL; expiry is evaluated immediately on
        // the one candidate and fails closed for both providers.
        if (credential is null || credential.ExpiresAtUtc <= now)
        {
            return null;
        }

        var localOwner = await db.Users.SingleOrDefaultAsync(user => user.PrincipalId == credential.OwnerPrincipalId, cancellationToken).ConfigureAwait(false);
        if (localOwner is not null && !localOwner.IsEnabled)
        {
            return null;
        }

        credential.LastUsedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(credential.Id, credential.OwnerPrincipalId, credential.Purpose,
            credential.Grants.Select(grant => new IntegrationCredentialGrantRequest(grant.TenantId, grant.Permission)).ToArray())
        {
            Resource = credential.Resource
        };
    }

    public async Task<VerifiedIntegrationCredential?> VerifyCurrentAsync(
        string credentialId,
        IntegrationCredentialPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentialId))
            return null;

        var now = DateTimeOffset.UtcNow;
        var credential = await db.IntegrationCredentials
            .Include(candidate => candidate.Grants)
            .SingleOrDefaultAsync(candidate => candidate.Id == credentialId && candidate.Purpose == purpose &&
                candidate.RevokedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (credential is null || credential.ExpiresAtUtc <= now)
            return null;

        var localOwner = await db.Users.SingleOrDefaultAsync(user => user.PrincipalId == credential.OwnerPrincipalId, cancellationToken).ConfigureAwait(false);
        if (localOwner is not null && !localOwner.IsEnabled)
            return null;

        return new(credential.Id, credential.OwnerPrincipalId, credential.Purpose,
            credential.Grants.Select(grant => new IntegrationCredentialGrantRequest(grant.TenantId, grant.Permission)).ToArray())
        {
            Resource = credential.Resource
        };
    }

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
